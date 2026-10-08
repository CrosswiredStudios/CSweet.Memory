namespace CSweet.Memory.Tests;

public sealed class TemporalTraversalTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FutureLinksCannotBridgeToCurrentFactsAndAsOfUsesValidTime(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-time-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "time-contract");
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        try
        {
            // Late-arriving evidence was valid earlier; recorded time is not AsOf.
            var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "evidence", "text/plain",
                new("user", "fixture"), "checksum", now.AddDays(-2), now.AddDays(7), ExpiresAt: now.AddDays(2));
            await store.AppendEpisodeAsync(source);
            var alpha = new MemoryEntity(Guid.NewGuid(), partition, "topic", "Alpha", [], null, false, now, now)
                { Sensitivity = MemorySensitivity.Internal };
            var beta = alpha with { Id = Guid.NewGuid(), CanonicalName = "Beta" };
            var gamma = alpha with { Id = Guid.NewGuid(), CanonicalName = "Gamma" };
            foreach (var entity in new[] { alpha, beta, gamma }) await store.UpsertEntityAsync(entity);
            var bridge = new MemoryEdge(Guid.NewGuid(), partition, source.Id, alpha.Id, "links", beta.Id,
                MemoryTrustTier.UnconfirmedUser, 1, now.AddDays(1), null, false, now);
            var onward = bridge with { Id = Guid.NewGuid(), FromEntityId = beta.Id, ToEntityId = gamma.Id, ValidFrom = now.AddDays(-1) };
            await store.WriteEdgeAsync(bridge);
            await store.WriteEdgeAsync(onward);
            async Task<IReadOnlyList<MemoryCandidate>> Graph(DateTimeOffset asOf) => await store.SearchAsync(new(partition,
                MemoryScope.Application, "Alpha", AsOf: asOf, Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic }));
            Assert.Empty(await Graph(now));
            var atStart = await Graph(now.AddDays(1));
            Assert.Equal(2, atStart.Count);
            Assert.Contains(atStart, x => x.Id == bridge.Id);
            Assert.Contains(atStart, x => x.Id == onward.Id);
            Assert.Empty(await Graph(now.AddDays(2)));
            var claim = new MemoryClaim(Guid.NewGuid(), partition, source.Id, alpha.Id, "chose", null, "old value",
                MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
                1, 1, now.AddDays(-1), now, now.AddDays(7));
            await store.WriteClaimAsync(claim);
            Assert.Contains(await Graph(now.AddHours(-1)), x => x.Id == claim.Id);
            Assert.DoesNotContain(await Graph(now), x => x.Id == claim.Id);
            var block = new MemoryBlock(Guid.NewGuid(), partition, "Core", "Future summary", 1, 100, true,
                MemoryTrustTier.Authoritative, now.AddDays(1)) { Sensitivity = MemorySensitivity.Internal };
            await store.WriteBlockAsync(block);
            Assert.Empty(await store.SearchAsync(new(partition, MemoryScope.Application, "Alpha", AsOf: now,
                Layers: new HashSet<MemoryLayer> { MemoryLayer.Core })));
            Assert.Empty(MemoryReadProjection.Create(await store.ExportAsync(partition), partition, MemorySensitivity.Internal, now).Blocks);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            await store.DisposeAsync();
            if (provider == "sqlite") foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
