using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class LexicalRetrievalRegressionTests
{
    public static IEnumerable<object[]> AliasCases => ProvenanceStoreTests.Providers.Cast<object[]>()
        .SelectMany(provider => new[] { "空", "北極", "café" }.Select(alias => new object[] { provider[0], alias }));
    public static IEnumerable<object[]> RankingCases => ProvenanceStoreTests.Providers.Cast<object[]>()
        .SelectMany(provider => new[] { "OPS-63", "RLS-812" }.Select(identifier => new object[] { provider[0], identifier }));

    [Theory]
    [MemberData(nameof(AliasCases))]
    public async Task DecodedUnicodeAliasesFindSourceLinkedClaims(string provider, string alias)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var (entity, claim) = await fixture.AddClaimAsync(alias);
        Assert.Contains(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, alias, Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        await fixture.Store.UpsertEntityAsync(entity with { Aliases = ["湖畔"] });
        Assert.DoesNotContain(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, alias, Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        Assert.Contains(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "湖畔", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
    }

    [Theory]
    [MemberData(nameof(RankingCases))]
    public async Task ConciseIdentifierEvidenceSurvivesCandidateLimitAmongIncidentalMentions(string provider, string identifier)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var target = fixture.Episode(identifier + " recovery uses verified snapshots.") with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") };
        await fixture.Store.AppendEpisodeAsync(target);
        for (var i = 0; i < 32; i++)
            await fixture.Store.AppendEpisodeAsync(fixture.Episode(identifier + " meeting discussed " + string.Join(' ', Enumerable.Repeat("coffee seating deliveries stationery", 6))));
        // Invalid shorter evidence must be filtered before ranking/limit, rather than crowding out the target.
        await fixture.Store.AppendEpisodeAsync(fixture.Episode(identifier) with { ExpiresAt = fixture.Now.AddTicks(-1) });
        await fixture.Store.AppendEpisodeAsync(fixture.Episode(identifier) with { OccurredAt = fixture.Now.AddDays(1) });
        foreach (var query in new[] { identifier, identifier + " " + string.Join(' ', Enumerable.Repeat("noise", 10000)) })
        {
            var results = await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, query, Limit: 1, AsOf: fixture.Now, Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
            Assert.Equal(target.Id, Assert.Single(results).Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulatedEscapedAliasUpgradeIsAtomicRepeatableAndKeepsLegacyWritesIndexed(bool injectFailure)
    {
        await using var fixture = await Fixture.CreateAsync("sqlite");
        var (entity, claim) = await fixture.AddClaimAsync("北極");
        async Task Sql(string sql, string? payload = null)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (payload is not null) command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }
        await Sql("""
            DELETE FROM memory_schema_migrations WHERE id='decoded-entity-aliases-v2';
            DROP TRIGGER memory_entities_fts_insert;
            DROP TRIGGER memory_entities_fts_update;
            CREATE TRIGGER memory_entities_fts_insert AFTER INSERT ON memory_entities BEGIN
                INSERT INTO memory_entities_fts(id,content) VALUES(new.id,new.canonical_name || ' ' || json_extract(new.payload,'$.aliases'));
            END;
            CREATE TRIGGER memory_entities_fts_update AFTER UPDATE ON memory_entities BEGIN
                DELETE FROM memory_entities_fts WHERE id=old.id;
                INSERT INTO memory_entities_fts(id,content) VALUES(new.id,new.canonical_name || ' ' || json_extract(new.payload,'$.aliases'));
            END;
            DELETE FROM memory_entities_fts;
            INSERT INTO memory_entities_fts(id,content) SELECT id,canonical_name || ' ' || json_extract(payload,'$.aliases') FROM memory_entities;
            """);
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "北極", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })));
        if (injectFailure)
        {
            await Sql("CREATE TRIGGER reject_alias_migration BEFORE INSERT ON memory_schema_migrations WHEN new.id='decoded-entity-aliases-v2' BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;");
            await using var failing = new SqliteMemoryStore(fixture.Path!);
            await Assert.ThrowsAsync<SqliteException>(() => failing.InitializeAsync());
            Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "北極", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })));
            await Sql("DROP TRIGGER reject_alias_migration;");
        }
        await using var upgraded = new SqliteMemoryStore(fixture.Path!);
        await using var concurrent = new SqliteMemoryStore(fixture.Path!);
        await Task.WhenAll(upgraded.InitializeAsync(), concurrent.InitializeAsync());
        Assert.Contains(await upgraded.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "北極", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        // A previous-version writer only knows the payload column, not the new index expression.
        var payload = JsonSerializer.Serialize(entity with { Aliases = ["湖畔", "café"] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Sql("UPDATE memory_entities SET payload=$payload", payload);
        Assert.Contains(await concurrent.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "湖畔", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        Assert.DoesNotContain(await upgraded.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "北極", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        await using var restarted = new SqliteMemoryStore(fixture.Path!);
        Assert.Contains(await restarted.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "café", Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })), x => x.Id == claim.Id);
        Assert.Equal(entity.Id, Assert.Single((await restarted.ExportAsync(fixture.Partition)).Entities).Id);
    }

    private sealed class Fixture(IMemoryStore store, string? path) : IAsyncDisposable
    {
        public IMemoryStore Store { get; } = store;
        public string? Path { get; } = path;
        public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString(), "retrieval-regression", "employee");
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public static async Task<Fixture> CreateAsync(string provider)
        {
            var path = provider == "sqlite" ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retrieval-{Guid.NewGuid():N}.db") : null;
            IMemoryStore store = path is not null ? new SqliteMemoryStore(path)
                : new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!);
            await store.InitializeAsync();
            return new(store, path);
        }
        public MemoryEpisode Episode(string content) => new(Guid.NewGuid(), Partition, MemoryScope.Agent, content, "text/plain", new("user", "fixture"), "checksum", Now.AddDays(-1), Now.AddDays(-1));
        public async Task<(MemoryEntity Entity, MemoryClaim Claim)> AddClaimAsync(string alias)
        {
            var episode = Episode("The localization owner selected the release language.");
            await Store.AppendEpisodeAsync(episode);
            var entity = new MemoryEntity(Guid.NewGuid(), Partition, "person", "Localization owner", [alias], null, false, Now.AddDays(-1), Now.AddDays(-1))
                { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [episode.Id] };
            await Store.UpsertEntityAsync(entity);
            var claim = new MemoryClaim(Guid.NewGuid(), Partition, episode.Id, entity.Id, "selected", null, "release language", MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1, Now.AddDays(-1), null, Now.AddDays(-1));
            await Store.WriteClaimAsync(claim);
            return (entity, claim);
        }
        public async ValueTask DisposeAsync()
        {
            await Store.DeleteScopeAsync(Partition);
            await Store.DisposeAsync();
            if (Path is not null) foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
        }
    }
}
