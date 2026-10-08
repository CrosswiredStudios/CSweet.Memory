using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class PostgreSqlTransactionTests
{
    [PostgresFact]
    public async Task ErasureEnlistsWithHostRollbackAndConcurrentDecisionsReplayOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!;
        await using var observer = new PostgreSqlMemoryStore(connectionString);
        await observer.InitializeAsync();
        var partition = new MemoryPartition(Guid.NewGuid().ToString("N"), "erasure-transaction", "owner");
        var now = DateTimeOffset.UtcNow;
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Agent, "erase atomically", "text/plain",
            new("test", "source"), "checksum", now, now);
        await observer.AppendEpisodeAsync(episode);
        var preview = await observer.PreviewEpisodeErasureAsync(partition, episode.Id);
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var enlisted = new PostgreSqlMemoryStore(transaction))
            {
                await enlisted.EraseEpisodeAsync(partition, episode.Id, preview.EvidenceToken);
                Assert.Empty((await enlisted.ExportAsync(partition)).Episodes);
                Assert.Single((await observer.ExportAsync(partition)).Episodes);
            }
            await transaction.RollbackAsync();
        }
        Assert.Single((await observer.ExportAsync(partition)).Episodes);
        await using var competing = new PostgreSqlMemoryStore(connectionString); await competing.InitializeAsync();
        var results = await Task.WhenAll(observer.EraseEpisodeAsync(partition, episode.Id, preview.EvidenceToken),
            competing.EraseEpisodeAsync(partition, episode.Id, preview.EvidenceToken));
        Assert.Single(results, x => !x.WasReplay); Assert.Single(results, x => x.WasReplay);
        Assert.Empty((await observer.ExportAsync(partition)).Episodes);
    }

    [PostgresFact]
    public async Task CallerTransactionControlsVisibilityRollbackCommitAndDisposal()
    {
        var connectionString = Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!;
        await using var observer = new PostgreSqlMemoryStore(connectionString);
        await observer.InitializeAsync();
        var partition = new MemoryPartition(Guid.NewGuid().ToString("N"), "transaction-test", "owner");
        var now = DateTimeOffset.UtcNow;
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Agent, "transaction memory", "text/plain",
            new("test", "source"), "checksum", now, now, Sensitivity: MemorySensitivity.Internal);
        var entity = new MemoryEntity(Guid.NewGuid(), partition, "learned:topic", "transaction memory", [], null, false, now, now)
            { Sensitivity = MemorySensitivity.Internal };
        var claim = new MemoryClaim(Guid.NewGuid(), partition, episode.Id, entity.Id, "transaction", null, "memory",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, 1, 1, now, null, now);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            foreach (var commit in new[] { false, true })
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var enlisted = new PostgreSqlMemoryStore(transaction))
                {
                    await enlisted.AppendEpisodeAsync(episode);
                    await enlisted.UpsertEntityAsync(entity);
                    await enlisted.WriteClaimAsync(claim);
                    Assert.Single((await enlisted.ExportAsync(partition)).Claims);
                    Assert.Empty((await observer.ExportAsync(partition)).Claims);
                }
                // Disposing an enlisted store must leave the caller's connection and transaction usable.
                if (commit) await transaction.CommitAsync();
                else await transaction.RollbackAsync();
                var exported = await observer.ExportAsync(partition);
                Assert.Equal(commit ? 1 : 0, exported.Episodes.Count);
                Assert.Equal(commit ? 1 : 0, exported.Entities.Count);
                Assert.Equal(commit ? 1 : 0, exported.Claims.Count);
            }
            await using var deletion = await connection.BeginTransactionAsync();
            await using (var enlisted = new PostgreSqlMemoryStore(deletion))
            {
                await enlisted.DeleteScopeAsync(partition);
                Assert.Empty((await enlisted.ExportAsync(partition)).Episodes);
            }
            await deletion.RollbackAsync();
            Assert.Single((await observer.ExportAsync(partition)).Claims);
        }
        finally { await observer.DeleteScopeAsync(partition); }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")))
            Skip = "Set CSWEET_MEMORY_TEST_POSTGRES to a dedicated test database.";
    }
}
