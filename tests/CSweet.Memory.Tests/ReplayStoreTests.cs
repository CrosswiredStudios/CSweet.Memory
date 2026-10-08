namespace CSweet.Memory.Tests;

public sealed class ReplayStoreTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentReplaysPreserveFirstWriteAndConflictsCannotUndoReview(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-replay-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "replay-contract");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "Original evidence",
                "text/plain", new("user", "source"), "checksum", now, now);
            await store.AppendEpisodeAsync(episode);
            var entity = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now);
            await store.UpsertEntityAsync(entity);
            var claim = new MemoryClaim(Guid.NewGuid(), partition, episode.Id, entity.Id, "uses", null, "PostgreSQL",
                MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal,
                1, 1, now, null, now);
            var writes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.WriteClaimAsync(claim)));
            Assert.Single(writes, result => result.Created);
            Assert.False((await store.WriteClaimAsync(claim with { RecordedAt = now.AddMinutes(1) })).Created);
            Assert.Equal(now, (await store.GetClaimAsync(claim.Id))!.RecordedAt);
            await Conflict(() => store.WriteClaimAsync(claim with { Value = "Conflicting content" }));
            await Conflict(() => store.WriteClaimAsync(claim with { Partition = partition with { UserId = "foreign" } }));
            await store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
            await Conflict(() => store.WriteClaimAsync(claim));
            Assert.Equal(MemoryConfirmationState.Confirmed, (await store.GetClaimAsync(claim.Id))!.Confirmation);

            var procedure = new ProceduralMemory(Guid.NewGuid(), partition, episode.Id, "Restore", "Verify backup first",
                "incident", 1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, now, null, now);
            Assert.True((await store.WriteProcedureAsync(procedure)).Created);
            Assert.False((await store.WriteProcedureAsync(procedure with { RecordedAt = now.AddMinutes(1) })).Created);
            await Conflict(() => store.WriteProcedureAsync(procedure with { Procedure = "Skip verification" }));
            var edge = new MemoryEdge(Guid.NewGuid(), partition, episode.Id, entity.Id, "relates", entity.Id,
                MemoryTrustTier.AgentInference, 1, now, null, true, now);
            Assert.True((await store.WriteEdgeAsync(edge)).Created);
            Assert.False((await store.WriteEdgeAsync(edge)).Created);
            await Conflict(() => store.WriteEdgeAsync(edge with { ValidTo = now.AddDays(1) }));
            var embedding = new MemoryEmbedding(Guid.NewGuid(), partition, episode.Id, MemoryLayer.Episodic, [1, 0], "test", now);
            Assert.True((await store.WriteEmbeddingAsync(embedding)).Created);
            Assert.False((await store.WriteEmbeddingAsync(embedding)).Created);
            await Conflict(() => store.WriteEmbeddingAsync(embedding with { Vector = [0, 1] }));
            var exported = await store.ExportAsync(partition);
            Assert.Single(exported.Claims);
            Assert.Single(exported.Procedures);
            Assert.Single(exported.Edges);
            Assert.NotNull(exported.Embeddings);
            Assert.Single(exported.Embeddings);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            await store.DisposeAsync();
            if (provider == "sqlite")
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static async Task Conflict(Func<Task<MemoryWriteResult>> write) =>
        Assert.Equal("memory_write_conflict", (await Assert.ThrowsAsync<InvalidOperationException>(write)).Message);
}
