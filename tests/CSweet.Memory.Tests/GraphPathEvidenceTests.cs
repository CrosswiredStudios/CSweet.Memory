namespace CSweet.Memory.Tests;

public sealed partial class ProvenanceStoreTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidBridgeContributorsCannotDiscoverOtherwiseValidDownstreamEdges(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var direct = await fixture.EpisodeAsync();
        var root = await fixture.EntityAsync("Alpha");
        var invalid = new[] { Guid.NewGuid(), (await fixture.EpisodeAsync(partition: fixture.Foreign)).Id,
            (await fixture.EpisodeAsync(expired: true)).Id, (await fixture.EpisodeAsync(future: true)).Id };
        for (var index = 0; index < invalid.Length; index++)
        {
            var middle = await fixture.EntityAsync($"Middle{index}");
            var end = await fixture.EntityAsync($"End{index}");
            await fixture.Store.WriteEdgeAsync(fixture.Edge(direct, root, middle) with { SourceEpisodeIds = [invalid[index]] });
            await fixture.Store.WriteEdgeAsync(fixture.Edge(direct, middle, end));
        }
        Assert.Empty(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Alpha",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic })));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PathsCarryAllEvidenceRestrictionsTrustAndValidityAndPreferAnIndependentLessRestrictedRoute(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var direct = await fixture.EpisodeAsync();
        var secret = await fixture.EpisodeAsync(MemorySensitivity.Confidential);
        var rootEvidence = await fixture.EpisodeAsync();
        var root = await fixture.EntityAsync("Alpha");
        await fixture.Store.UpsertEntityAsync(root with { SourceEpisodeIds = [rootEvidence.Id] });
        var middle = await fixture.EntityAsync("Beta");
        var end = await fixture.EntityAsync("Gamma");
        var bridge = fixture.Edge(secret, root, middle) with { Trust = MemoryTrustTier.External, Confidence = 0.2,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5), ValidTo = DateTimeOffset.UtcNow.AddMinutes(5) };
        var onward = fixture.Edge(direct, middle, end) with { Trust = MemoryTrustTier.Authoritative };
        await fixture.Store.WriteEdgeAsync(bridge); await fixture.Store.WriteEdgeAsync(onward);
        Task<IReadOnlyList<MemoryCandidate>> Search() => fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Alpha",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic }));
        var candidate = Assert.Single(await Search(), x => x.Id == onward.Id);
        Assert.Equal(MemorySensitivity.Confidential, candidate.Sensitivity);
        Assert.Equal(MemoryTrustTier.External, candidate.Trust); Assert.Equal(0.2, candidate.Score);
        Assert.Equal(bridge.ValidFrom, candidate.ValidFrom); Assert.Equal(bridge.ValidTo, candidate.ValidTo);
        Assert.Equal(new[] { direct.Id, secret.Id, rootEvidence.Id }.Order(), candidate.EpisodeIds.Order());
        await fixture.Store.WriteEdgeAsync(fixture.Edge(direct, root, middle));
        candidate = Assert.Single(await Search(), x => x.Id == onward.Id);
        Assert.Equal(MemorySensitivity.Internal, candidate.Sensitivity);
        Assert.DoesNotContain(secret.Id, candidate.EpisodeIds); Assert.Contains(rootEvidence.Id, candidate.EpisodeIds);
        // Entity evidence invalidation must also prevent traversal, even with two valid edge sources.
        await fixture.Store.UpsertEntityAsync(root with { SourceEpisodeIds = [Guid.NewGuid()] });
        Assert.Empty(await Search());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ThreeHopTraversalIsBoundedAndKeepsAllPathSourcesAcrossCycles(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var entities = new List<MemoryEntity>(); var sources = new List<MemoryEpisode>(); var edges = new List<MemoryEdge>();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" }) entities.Add(await fixture.EntityAsync(name));
        for (var index = 0; index < 4; index++)
        {
            var source = await fixture.EpisodeAsync(); sources.Add(source);
            var edge = fixture.Edge(source, entities[index], entities[index + 1]); edges.Add(edge);
            await fixture.Store.WriteEdgeAsync(edge);
        }
        await fixture.Store.WriteEdgeAsync(fixture.Edge(sources[0], entities[1], entities[0]));
        var results = await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Alpha",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic }));
        Assert.DoesNotContain(results, x => x.Id == edges[3].Id);
        Assert.Equal(sources.Take(3).Select(x => x.Id).Order(), Assert.Single(results, x => x.Id == edges[2].Id).EpisodeIds.Order());
        Assert.Equal(results.Count, results.Select(x => x.Id).Distinct().Count());
    }
}
