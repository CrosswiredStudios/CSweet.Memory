using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class CoreSearchTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;
    public static IEnumerable<object[]> UpgradeCases => Providers.Cast<object[]>().SelectMany(x =>
        new[] { false, true }.Select(fail => new object[] { x[0], fail }));

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TopicSearchFindsNameOrContentBeforeLimitAmongManyUnrelatedPinnedBlocks(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        for (var i = 0; i < 110; i++)
            await fixture.Store.WriteBlockAsync(fixture.Block("Inventory " + i, "Warehouse count and pallet locations")
                with { Id = new Guid(i + 1, 0, 0, new byte[8]) });
        var target = fixture.Block("Delivery preference", "Friday digest begins with unresolved blockers.")
            with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") };
        await fixture.Store.WriteBlockAsync(target);
        foreach (var query in new[] { "Friday digest", "Delivery preference" })
            Assert.Equal(target.Id, Assert.Single(await fixture.SearchAsync(query, limit: 1)).Id);
        Assert.Empty(await fixture.SearchAsync("saffron insurance"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExplicitContextAssemblyRetainsPinnedPreferencesWithoutAddingUnrelatedUnpinnedBlocks(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var pinned = fixture.Block("Response style", "Use concise responses with explicit owners.");
        var unpinned = fixture.Block("Old workshop", "Prior workshop seating notes.") with { IsPinned = false };
        await fixture.Store.WriteBlockAsync(pinned);
        await fixture.Store.WriteBlockAsync(unpinned);
        Assert.Empty(await fixture.SearchAsync("hello"));
        Assert.Equal(pinned.Id, Assert.Single(await fixture.SearchAsync("hello", includePinned: true)).Id);
        var engine = new MemoryEngine(fixture.Store, Options.Create(new AgentMemoryOptions()),
            authorizer: new AllowAllMemoryScopeAuthorizer(), redactor: new PassthroughMemoryRedactor());
        var context = await engine.RecallAsync(new(fixture.Partition, MemoryScope.Agent, "hello",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Core }));
        Assert.Equal(pinned.Id, Assert.Single(context.Items).Id);
        Assert.Equal(unpinned.Id, Assert.Single(await fixture.SearchAsync("workshop")).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TemporalConfirmationAndPartitionFiltersPrecedeCoreResultLimit(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var target = fixture.Block("Current policy", "Delivery approvals require owner signoff.")
            with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") };
        var future = target with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Future policy", UpdatedAt = fixture.Now.AddTicks(1) };
        var pending = target with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "Pending policy", Confirmation = MemoryConfirmationState.Pending };
        var foreign = target with { Id = Guid.NewGuid(), Partition = fixture.Partition with { AgentId = "other" }, Name = "Foreign policy", SourceEpisodeIds = [] };
        await fixture.Store.WriteBlockAsync(future);
        await fixture.Store.WriteBlockAsync(pending);
        await fixture.Store.WriteBlockAsync(foreign);
        await fixture.Store.WriteBlockAsync(target);
        foreach (var includePinned in new[] { false, true })
            Assert.Equal(target.Id, Assert.Single(await fixture.SearchAsync("approvals", limit: 1, includePinned: includePinned)).Id);
        Assert.Contains(await fixture.SearchAsync("approvals", includePending: true), x => x.Id == pending.Id);
        Assert.Contains(await fixture.SearchAsync("approvals", asOf: fixture.Now.AddTicks(1)), x => x.Id == future.Id);
        await fixture.Store.DeleteScopeAsync(foreign.Partition);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QueryAndPinnedPathsPreserveContributorSensitivityAndSuppression(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var episode = fixture.Source with { Id = Guid.NewGuid(), Sensitivity = MemorySensitivity.Restricted, IdempotencyKey = "restricted" };
        await fixture.Store.AppendEpisodeAsync(episode);
        var block = fixture.Block("Response policy", "Always identify the decision owner.") with { SourceEpisodeIds = [episode.Id] };
        await fixture.Store.WriteBlockAsync(block);
        foreach (var includePinned in new[] { false, true })
        {
            var candidate = Assert.Single(await fixture.SearchAsync("decision", includePinned: includePinned));
            Assert.Equal(MemorySensitivity.Restricted, candidate.Sensitivity);
            Assert.Contains(episode.Id, candidate.EpisodeIds);
        }
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(fixture.Partition, episode.Id);
        Assert.Empty(await fixture.SearchAsync("decision"));
        Assert.Empty(await fixture.SearchAsync("hello", includePinned: true));
    }

    [Theory]
    [MemberData(nameof(UpgradeCases))]
    public async Task PopulatedUpgradeIsAtomicConcurrentRepeatableAndIndexesLegacyWrites(string provider, bool injectFailure)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var target = fixture.Block("Localized preference", "Montréal 한국어 digest.");
        await fixture.Store.WriteBlockAsync(target);
        await fixture.RemoveCoreMigrationAsync();
        if (injectFailure)
        {
            await fixture.InjectFailureAsync();
            await using var failing = fixture.OpenStore();
            await Assert.ThrowsAnyAsync<Exception>(() => failing.InitializeAsync());
            Assert.Equal(0, await fixture.MarkerCountAsync());
            Assert.False(await fixture.CoreIndexExistsAsync());
            Assert.Equal(target.Content, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Blocks).Content);
            await fixture.ClearFailureAsync();
        }
        await using var upgraded = fixture.OpenStore();
        await using var concurrent = fixture.OpenStore();
        await Task.WhenAll(upgraded.InitializeAsync(), concurrent.InitializeAsync());
        Assert.Equal(1, await fixture.MarkerCountAsync());
        Assert.True(await fixture.CoreIndexExistsAsync());
        Assert.Equal(target.Id, Assert.Single(await fixture.SearchAsync("한국어", store: upgraded)).Id);
        var changed = target with { Content = "café 東京 preference.", Name = "New preference" };
        await fixture.LegacyUpdateAsync(changed);
        Assert.Empty(await fixture.SearchAsync("한국어", store: concurrent));
        foreach (var query in new[] { "東京", "New preference" })
            Assert.Equal(target.Id, Assert.Single(await fixture.SearchAsync(query, store: concurrent)).Id);
        await using var restarted = fixture.OpenStore();
        await restarted.InitializeAsync();
        Assert.Equal(target.Id, Assert.Single(await fixture.SearchAsync("café", store: restarted)).Id);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        Assert.Empty(await fixture.SearchAsync("東京", store: restarted));
    }

    private sealed class Fixture(string provider, string location, string? database) : IAsyncDisposable
    {
        public IMemoryStore Store { get; private set; } = null!;
        public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString("D"), "core-search-test", "employee");
        public DateTimeOffset Now { get; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public MemoryEpisode Source { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string provider)
        {
            string? database = null;
            string location;
            if (provider == "sqlite") location = Path.Combine(Path.GetTempPath(), $"csweet-core-{Guid.NewGuid():N}.db");
            else
            {
                database = "csweet_core_" + Guid.NewGuid().ToString("N");
                var configured = Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES");
                location = new NpgsqlConnectionStringBuilder(configured) { Database = database, Pooling = false }.ConnectionString;
                await using var connection = new NpgsqlConnection(configured);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
            var fixture = new Fixture(provider, location, database);
            try
            {
                fixture.Store = fixture.OpenStore();
                await fixture.Store.InitializeAsync();
                fixture.Source = new(Guid.NewGuid(), fixture.Partition, MemoryScope.Agent, "Confirmed employee response and delivery preferences.",
                    "text/plain", new("user", "fixture"), "source", fixture.Now.AddDays(-1), fixture.Now.AddDays(-1));
                await fixture.Store.AppendEpisodeAsync(fixture.Source);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public IMemoryStore OpenStore() => provider == "sqlite" ? new SqliteMemoryStore(location) : new PostgreSqlMemoryStore(location);
        public MemoryBlock Block(string name, string content) => new(Guid.NewGuid(), Partition, name, content, 1, 256,
            true, MemoryTrustTier.ConfirmedUser, Now.AddMinutes(-1)) { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [Source.Id] };
        public Task<IReadOnlyList<MemoryCandidate>> SearchAsync(string query, int limit = 20, bool includePinned = false,
            bool includePending = false, DateTimeOffset? asOf = null, IMemoryStore? store = null) => (store ?? Store).SearchAsync(
                new(Partition, MemoryScope.Agent, query, limit, asOf ?? Now, new HashSet<MemoryLayer> { MemoryLayer.Core }, IncludePending: includePending)
                { IncludePinnedCore = includePinned });
        public Task RemoveCoreMigrationAsync() => SqlAsync(provider == "sqlite" ? """
            DELETE FROM memory_schema_migrations WHERE id='indexed-core-search-v1';
            DROP TRIGGER memory_blocks_fts_insert; DROP TRIGGER memory_blocks_fts_update; DROP TRIGGER memory_blocks_fts_delete;
            DROP TABLE memory_blocks_fts;
            """ : """
            DELETE FROM csweet_memory_schema_migrations WHERE id='indexed-core-search-v1';
            ALTER TABLE csweet_memory_blocks DROP COLUMN search_vector;
            """);
        public Task InjectFailureAsync() => SqlAsync(provider == "sqlite" ? """
            CREATE TRIGGER reject_core_migration BEFORE INSERT ON memory_schema_migrations
              WHEN new.id='indexed-core-search-v1' BEGIN SELECT RAISE(ABORT,'injected core upgrade failure'); END;
            """ : """
            CREATE FUNCTION reject_core_migration() RETURNS trigger LANGUAGE plpgsql AS $$
              BEGIN IF NEW.id='indexed-core-search-v1' THEN RAISE EXCEPTION 'injected core upgrade failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_core_migration BEFORE INSERT ON csweet_memory_schema_migrations FOR EACH ROW EXECUTE FUNCTION reject_core_migration();
            """);
        public Task ClearFailureAsync() => SqlAsync(provider == "sqlite" ? "DROP TRIGGER reject_core_migration;" :
            "DROP TRIGGER reject_core_migration ON csweet_memory_schema_migrations; DROP FUNCTION reject_core_migration();");
        public async Task<int> MarkerCountAsync() => Convert.ToInt32(await ScalarAsync(provider == "sqlite"
            ? "SELECT count(*) FROM memory_schema_migrations WHERE id='indexed-core-search-v1'"
            : "SELECT count(*) FROM csweet_memory_schema_migrations WHERE id='indexed-core-search-v1'"));
        public async Task<bool> CoreIndexExistsAsync() => Convert.ToInt32(await ScalarAsync(provider == "sqlite"
            ? "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='memory_blocks_fts'"
            : "SELECT count(*) FROM information_schema.columns WHERE table_name='csweet_memory_blocks' AND column_name='search_vector'")) == 1;
        public Task LegacyUpdateAsync(MemoryBlock block) => SqlAsync(provider == "sqlite"
            ? "UPDATE memory_blocks SET name=$name,payload=$payload WHERE id=$id"
            : "UPDATE csweet_memory_blocks SET name=@name,payload=@payload::jsonb WHERE id=@id", block);
        private async Task SqlAsync(string sql, MemoryBlock? block = null) => _ = await CommandAsync(sql, block, scalar: false);
        private Task<object?> ScalarAsync(string sql) => CommandAsync(sql, null, scalar: true);
        private async Task<object?> CommandAsync(string sql, MemoryBlock? block, bool scalar)
        {
            if (provider == "sqlite")
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = location, Pooling = false }.ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand(); command.CommandText = sql;
                if (block is not null) { command.Parameters.AddWithValue("$id", block.Id.ToString("D")); command.Parameters.AddWithValue("$name", block.Name);
                    command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(block, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
                return scalar ? await command.ExecuteScalarAsync() : await command.ExecuteNonQueryAsync();
            }
            await using var pg = new NpgsqlConnection(location); await pg.OpenAsync();
            await using var pgCommand = new NpgsqlCommand(sql, pg);
            if (block is not null) { pgCommand.Parameters.AddWithValue("id", block.Id); pgCommand.Parameters.AddWithValue("name", block.Name);
                pgCommand.Parameters.AddWithValue("payload", JsonSerializer.Serialize(block, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
            return scalar ? await pgCommand.ExecuteScalarAsync() : await pgCommand.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (Store is not null) await Store.DisposeAsync();
            if (provider == "sqlite") foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(location + suffix);
            else
            {
                await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
                await connection.OpenAsync();
                // Owned database cleanup can wait behind concurrent CREATE DATABASE/checkpoint
                // activity. This bounded cleanup allowance does not change query timeouts.
                await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection) { CommandTimeout = 90 };
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
