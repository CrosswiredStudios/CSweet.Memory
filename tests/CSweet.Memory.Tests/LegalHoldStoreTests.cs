using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class LegalHoldStoreTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ScopeDeletionWithHeldEvidencePreservesAllRecords(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-held-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!) : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "legal-hold-test");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var held = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "held evidence", "text/plain",
                new("application", "fixture"), "checksum", now, now, LegalHold: true);
            await store.AppendEpisodeAsync(held);
            await store.AppendEpisodeAsync(held with { Id = Guid.NewGuid(), LegalHold = false, Content = "ordinary evidence" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(partition));
            Assert.Equal("memory_legal_hold_prevents_deletion", error.Message);
            var retained = await store.ExportAsync(partition);
            Assert.Equal(2, retained.Episodes.Count);
            Assert.True(retained.Episodes.Single(x => x.Id == held.Id).LegalHold);
        }
        finally
        {
            // Test-only removal of this randomly generated fixture; production callers cannot clear a hold this way.
            if (provider == "postgres")
            {
                await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("DELETE FROM csweet_memory_episodes WHERE partition_key=@partition", connection);
                command.Parameters.AddWithValue("partition", partition.StorageKey);
                await command.ExecuteNonQueryAsync();
            }
            await store.DisposeAsync();
            if (provider == "sqlite")
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
