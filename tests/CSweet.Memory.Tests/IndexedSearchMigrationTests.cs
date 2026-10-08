using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class IndexedSearchMigrationTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PopulatedUpgradeIsRepeatableAndLegacyWritesStayIndexed(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-index-{Guid.NewGuid():N}.db");
        var schema = $"memory_index_{Guid.NewGuid():N}";
        var original = Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES");
        var connectionString = provider == "postgres"
            ? new NpgsqlConnectionStringBuilder(original) { SearchPath = schema, Pooling = false }.ConnectionString
            : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
        async Task Sql(string sql)
        {
            await using System.Data.Common.DbConnection connection = provider == "postgres"
                ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        IMemoryStore OpenStore() => provider == "postgres" ? new PostgreSqlMemoryStore(connectionString) : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "migration-fixture");
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (provider == "postgres") await Sql($"CREATE SCHEMA {schema}");
            var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "source", "text/plain",
                new("user", "fixture"), "checksum", now, now);
            var entity = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now)
                { Sensitivity = MemorySensitivity.Internal };
            var claim = new MemoryClaim(Guid.NewGuid(), partition, episode.Id, entity.Id, "owns", null, "CSM-42",
                MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, 1, 1, now, null, now);
            var procedure = new ProceduralMemory(Guid.NewGuid(), partition, episode.Id, "Recovery", "Verify checksums",
                "billing outage", 1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now, null, now);
            await using (var initial = OpenStore())
            {
                await initial.AppendEpisodeAsync(episode);
                await initial.UpsertEntityAsync(entity);
                await initial.WriteClaimAsync(claim);
                await initial.WriteProcedureAsync(procedure);
            }
            // Restore the previous schema shape while retaining every populated row.
            if (provider == "postgres")
            {
                foreach (var table in new[] { "entities", "claims", "procedures" })
                    await Sql($"ALTER TABLE csweet_memory_{table} DROP COLUMN search_vector");
                await Sql("DELETE FROM csweet_memory_schema_migrations WHERE id='indexed-search-v1'");
            }
            else
            {
                foreach (var table in new[] { "entities", "claims", "procedures" })
                    await Sql($"DROP TRIGGER memory_{table}_fts_insert; DROP TRIGGER memory_{table}_fts_update; DROP TRIGGER memory_{table}_fts_delete; DROP TABLE memory_{table}_fts;");
                await Sql("DELETE FROM memory_schema_migrations WHERE id IN ('indexed-search-v1','decoded-entity-aliases-v2')");
            }
            await using var upgraded = OpenStore();
            await using var concurrent = OpenStore();
            await Task.WhenAll(upgraded.InitializeAsync(), concurrent.InitializeAsync());
            Assert.Contains(await upgraded.SearchAsync(new(partition, MemoryScope.Application, "CSM-42")), item => item.Id == claim.Id);
            Assert.Contains(await upgraded.SearchAsync(new(partition, MemoryScope.Application, "billing")), item => item.Id == procedure.Id);
            // Existing writers update payloads and names without knowing about new search fields.
            await upgraded.UpsertEntityAsync(entity with { Aliases = ["Aurora"] });
            Assert.Contains(await concurrent.SearchAsync(new(partition, MemoryScope.Application, "Aurora")), item => item.Id == claim.Id);
            var export = await upgraded.ExportAsync(partition);
            Assert.Equal(MemorySensitivity.Internal, Assert.Single(export.Claims).Sensitivity);
            Assert.Equal(episode.Id, Assert.Single(export.Episodes).Id);
            await using var restarted = OpenStore();
            Assert.Single(await restarted.SearchAsync(new(partition, MemoryScope.Application, "billing")), item => item.Id == procedure.Id);
        }
        finally
        {
            if (provider == "postgres") await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            else foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
