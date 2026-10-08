using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpdatesRetainSnapshotsAndBlockIdentityWhileReplayAddsNoRevision(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode();
        await store.AppendEpisodeAsync(source);
        var entity = fixture.Entity(source);
        await store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "prefers", null, "original",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1,
            source.OccurredAt, null, source.RecordedAt) { SourceEpisodeIds = [source.Id] };
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "preferences", "original", 88, 200, true,
            MemoryTrustTier.AgentInference, source.RecordedAt) { Sensitivity = MemorySensitivity.Personal, SourceEpisodeIds = [source.Id] };
        var edge = new MemoryEdge(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "knows", entity.Id,
            MemoryTrustTier.AgentInference, 1, source.OccurredAt, null, true, source.RecordedAt);
        var procedure = new ProceduralMemory(Guid.NewGuid(), fixture.Partition, source.Id, "test", "procedure", null,
            1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, source.OccurredAt, null, source.RecordedAt);
        var embedding = new MemoryEmbedding(Guid.NewGuid(), fixture.Partition, source.Id, MemoryLayer.Episodic, [1, 0], "test", source.RecordedAt);
        await store.WriteClaimAsync(claim); await store.WriteBlockAsync(block); await store.WriteEdgeAsync(edge);
        await store.WriteProcedureAsync(procedure); await store.WriteEmbeddingAsync(embedding);
        var reader = (IMemoryRevisionReader)store;
        var records = new[] { (MemoryRecordKind.Episode, source.Id), (MemoryRecordKind.Entity, entity.Id),
            (MemoryRecordKind.Claim, claim.Id), (MemoryRecordKind.Block, block.Id), (MemoryRecordKind.Edge, edge.Id),
            (MemoryRecordKind.Procedure, procedure.Id), (MemoryRecordKind.Embedding, embedding.Id) };
        foreach (var (kind, id) in records)
            Assert.Equal(MemoryRevisionOperation.Insert, Assert.Single((await reader.ReadRevisionsAsync(fixture.Partition, kind, id)).Items).Operation);
        await store.WriteClaimAsync(claim); await store.WriteBlockAsync(block); await store.UpsertEntityAsync(entity);
        Assert.Single((await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Claim, claim.Id)).Items);
        Assert.Single((await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Block, block.Id)).Items);
        await store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        await store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected);
        await store.UpsertEntityAsync(entity with { CanonicalName = "renamed", Aliases = ["original"] });
        var result = await store.WriteBlockAsync(block with { Id = Guid.NewGuid(), Content = "updated", Revision = 1 });
        Assert.Equal(block.Id, result.Id);
        var blocks = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Block, block.Id);
        Assert.Equal(new[] { "original", "updated" }, blocks.Items.Select(x => JsonSerializer.Deserialize<MemoryBlock>(x.PayloadJson, JsonOptions)!.Content));
        Assert.Equal(block.Id, Assert.Single((await store.ExportAsync(fixture.Partition)).Blocks).Id);
        var claims = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Claim, claim.Id);
        Assert.Equal(new[] { MemoryConfirmationState.Pending, MemoryConfirmationState.Confirmed, MemoryConfirmationState.Rejected },
            claims.Items.Select(x => JsonSerializer.Deserialize<MemoryClaim>(x.PayloadJson, JsonOptions)!.Confirmation));
        Assert.All(claims.Items, x => Assert.Equal(source.Id, Assert.Single(JsonSerializer.Deserialize<MemoryClaim>(x.PayloadJson, JsonOptions)!.SourceEpisodeIds)));
        Assert.Equal(3, claims.Items.Select(x => x.Revision).Distinct().Count());
        // Ordinary export remains a current-state projection, not an accidental history endpoint.
        Assert.Equal(MemoryConfirmationState.Rejected, Assert.Single((await store.ExportAsync(fixture.Partition)).Claims).Confirmation);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentChangesRemainOrderedAndHistoryCannotBeRewritten(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var entity = fixture.Entity(source); await store.UpsertEntityAsync(entity);
        var sources = Enumerable.Range(0, 6).Select(_ => fixture.Episode()).ToArray();
        foreach (var episode in sources) await store.AppendEpisodeAsync(episode);
        await Task.WhenAll(sources.Select(episode => Task.Run(() => store.UpsertEntityAsync(entity with { SourceEpisodeIds = [episode.Id] }))));
        var reader = (IMemoryRevisionReader)store;
        var history = (await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id)).Items;
        Assert.Equal(7, history.Count);
        Assert.Equal(history.Select(x => x.Revision).Order(), history.Select(x => x.Revision));
        Assert.Equal(Enumerable.Range(1, 7), history.Select(x => JsonSerializer.Deserialize<MemoryEntity>(x.PayloadJson, JsonOptions)!.SourceEpisodeIds.Count));
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.Sql($"UPDATE {fixture.Prefix}revisions SET operation=3 WHERE kind=1"));
        Assert.Equal(history.Select(x => x.PayloadJson), (await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id)).Items.Select(x => x.PayloadJson));
        // An old direct writer must not sever a record's history by replacing its primary ID.
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.Sql($"UPDATE {fixture.Prefix}entities SET id='{Guid.NewGuid():D}'"));
        Assert.Equal(entity.Id, Assert.Single((await store.ExportAsync(fixture.Partition)).Entities).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RevisionFailureRollsBackTheCorrespondingWrite(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var entity = fixture.Entity(source); await store.UpsertEntityAsync(entity);
        await fixture.Sql(fixture.Postgres ? $"""
            CREATE FUNCTION fail_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected history failure'; END $$;
            CREATE TRIGGER fail_revision BEFORE INSERT ON {fixture.Prefix}revisions FOR EACH ROW EXECUTE FUNCTION fail_revision();
            """ : $"CREATE TRIGGER fail_revision BEFORE INSERT ON {fixture.Prefix}revisions BEGIN SELECT RAISE(ABORT,'injected history failure'); END;");
        await Assert.ThrowsAnyAsync<DbException>(() => store.UpsertEntityAsync(entity with { CanonicalName = "must roll back" }));
        Assert.Equal(entity.CanonicalName, (await store.FindEntityByApplicationKeyAsync(fixture.Partition, entity.ApplicationKey!))!.CanonicalName);
        Assert.Single((await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id)).Items);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HistoryPaginationIsBoundedAndCannotCrossPartitions(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var entity = fixture.Entity(source); await store.UpsertEntityAsync(entity);
        for (var i = 0; i < 4; i++) await store.UpsertEntityAsync(entity with { CanonicalName = "version " + i });
        var reader = (IMemoryRevisionReader)store;
        var first = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id, limit: 2);
        var second = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id, first.NextAfterRevision!.Value, 2);
        var third = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id, second.NextAfterRevision!.Value, 2);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.Revision).Distinct().Count());
        Assert.Null(third.NextAfterRevision);
        Assert.Empty((await reader.ReadRevisionsAsync(fixture.Partition with { AgentId = null, UserId = fixture.Partition.AgentId }, MemoryRecordKind.Entity, entity.Id)).Items);
        Assert.Empty((await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Claim, entity.Id)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id, limit: 201));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadRevisionsAsync(fixture.Partition, (MemoryRecordKind)999, entity.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id, afterRevision: -1));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegalHoldPreservesHistoryAndScopePurgeRemovesHistoricalPayloads(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode() with { LegalHold = true }; await store.AppendEpisodeAsync(source);
        var entity = fixture.Entity(source); await store.UpsertEntityAsync(entity);
        await store.UpsertEntityAsync(entity with { CanonicalName = "updated" });
        var reader = (IMemoryRevisionReader)store;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(fixture.Partition));
        Assert.Equal(2, (await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id)).Items.Count);
        // Privileged fixture-only hold release; production review belongs to the lifecycle service.
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{legalHold}}','false')"
            : $"UPDATE {fixture.Prefix}episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false'))");
        await store.DeleteScopeAsync(fixture.Partition);
        Assert.Empty((await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Entity, entity.Id)).Items);
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}revisions")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HistoricalPageByteBudgetDoesNotSkipAnOversizedNextRevision(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "large", new string('x', 600_000), 1, 200_000, true,
            MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(block);
        await store.WriteBlockAsync(block with { Content = new string('y', 600_000) });
        var reader = (IMemoryRevisionReader)store;
        var first = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Block, block.Id);
        Assert.Single(first.Items); Assert.NotNull(first.NextAfterRevision);
        var second = await reader.ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Block, block.Id, first.NextAfterRevision!.Value);
        Assert.Single(second.Items); Assert.Null(second.NextAfterRevision);
        Assert.NotEqual(first.Items[0].Revision, second.Items[0].Revision);
        await store.WriteBlockAsync(block with { Content = new string('z', 1_048_576) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadRevisionsAsync(fixture.Partition,
            MemoryRecordKind.Block, block.Id, second.Items[0].Revision));
        Assert.Equal("memory_revision_page_limit", error.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TrustedDeletionRecordsTheLastSnapshotWithoutReturningItToRecall(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        await fixture.Sql($"DELETE FROM {fixture.Prefix}episodes");
        var history = await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id);
        Assert.Equal(new[] { MemoryRevisionOperation.Insert, MemoryRevisionOperation.Delete }, history.Items.Select(x => x.Operation));
        Assert.Equal(history.Items[0].PayloadJson, history.Items[1].PayloadJson);
        Assert.Empty((await store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Empty(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "source evidence")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PopulatedUpgradeEstablishesOneBaselineAndRecoversAnInterruptedInstall(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        var source = fixture.Episode() with { RecordedAt = DateTimeOffset.UtcNow.AddYears(-5) };
        await using (var initial = fixture.Store()) await initial.AppendEpisodeAsync(source);
        await fixture.RemoveHistory();
        // Force failure after history-table creation; the enclosing migration transaction must undo all of it.
        await fixture.Sql($"CREATE TABLE history_conflict(id integer); CREATE INDEX ix_{fixture.Prefix}revision_record ON history_conflict(id);");
        await using (var failed = fixture.Store()) await Assert.ThrowsAnyAsync<DbException>(() => failed.InitializeAsync());
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}schema_migrations WHERE id='revision-history-v1'")));
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar(fixture.Postgres
            ? "SELECT count(*) FROM information_schema.tables WHERE table_schema=current_schema() AND table_name='csweet_memory_revisions'"
            : "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='memory_revisions'")));
        await fixture.Sql("DROP TABLE history_conflict");
        await using var first = fixture.Store(); await using var second = fixture.Store();
        var beforeUpgrade = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        var baseline = Assert.Single((await ((IMemoryRevisionReader)first).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items);
        Assert.Equal(MemoryRevisionOperation.Baseline, baseline.Operation);
        Assert.InRange(baseline.RecordedAt, beforeUpgrade, DateTimeOffset.UtcNow);
        Assert.Equal(source.RecordedAt, JsonSerializer.Deserialize<MemoryEpisode>(baseline.PayloadJson, JsonOptions)!.RecordedAt);
        await using var restarted = fixture.Store();
        Assert.Single((await ((IMemoryRevisionReader)restarted).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items);
    }

    private sealed class Fixture(string provider, string path, string schema, string connectionString) : IAsyncDisposable
    {
        public bool Postgres => provider == "postgres";
        public string Prefix => Postgres ? "csweet_memory_" : "memory_";
        public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString(), "history-test", "owner");
        public IMemoryStore Store() => Postgres ? new PostgreSqlMemoryStore(connectionString) : new SqliteMemoryStore(path);
        public MemoryEpisode Episode() => new(Guid.NewGuid(), Partition, MemoryScope.Agent, "source evidence", "text/plain",
            new("user", "test"), "checksum", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        public MemoryEntity Entity(MemoryEpisode source) => new(Guid.NewGuid(), Partition, "person", "original", [], "known", false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Personal, SourceEpisodeIds = [source.Id] };
        public static async Task<Fixture> Create(string provider)
        {
            var path = Path.Combine(Path.GetTempPath(), $"memory-history-{Guid.NewGuid():N}.db");
            var schema = $"memory_history_{Guid.NewGuid():N}";
            var connection = provider == "postgres"
                ? new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")) { SearchPath = schema, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
            var fixture = new Fixture(provider, path, schema, connection);
            if (fixture.Postgres) await fixture.Sql($"CREATE SCHEMA {schema}");
            return fixture;
        }
        public async Task<object?> Scalar(string sql)
        {
            await using DbConnection connection = Postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        public async Task Sql(string sql)
        {
            await using DbConnection connection = Postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async Task RemoveHistory()
        {
            foreach (var table in new[] { "episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings" })
                await Sql(Postgres ? $"DROP TRIGGER {Prefix}{table}_revision ON {Prefix}{table}"
                    : $"DROP TRIGGER {Prefix}{table}_revision_insert; DROP TRIGGER {Prefix}{table}_revision_update; DROP TRIGGER {Prefix}{table}_revision_delete;");
            await Sql($"DROP TABLE {Prefix}revisions; DELETE FROM {Prefix}schema_migrations WHERE id='revision-history-v1';");
            if (Postgres) await Sql($"DROP FUNCTION {Prefix}record_revision(); DROP FUNCTION {Prefix}revision_immutable();");
        }
        public async ValueTask DisposeAsync()
        {
            if (Postgres) await Sql($"DROP SCHEMA {schema} CASCADE");
            else foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
