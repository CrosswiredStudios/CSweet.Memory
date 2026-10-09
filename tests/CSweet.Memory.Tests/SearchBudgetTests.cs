namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    public static IEnumerable<object[]> SearchPayloadBudgetCases => Providers.Cast<object[]>()
        .SelectMany(provider => new[] { false, true }.Select(combined => new object[] { provider[0], combined }));

    [Theory]
    [MemberData(nameof(SearchPayloadBudgetCases))]
    public async Task SearchReportsExhaustionInsteadOfAnEmptyAnswerAndSharesBudgetAcrossLayers(string provider, bool combined)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        await store.InitializeAsync();
        var source = fixture.Episode();
        await store.AppendEpisodeAsync(source);
        var body = "Sentinel " + new string('x', (combined ? 5 : 9) * 1024 * 1024);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Sentinel memory", body, 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow.AddSeconds(-1))
            { SourceEpisodeIds = [source.Id], Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(block);
        var layers = new HashSet<MemoryLayer> { MemoryLayer.Core };
        if (combined)
        {
            var procedure = new ProceduralMemory(Guid.NewGuid(), fixture.Partition, source.Id, "Sentinel procedure", body, null,
                1, MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, source.OccurredAt, null, source.RecordedAt);
            await store.WriteProcedureAsync(procedure);
            layers.Add(MemoryLayer.Procedural);
            // Each channel fits individually; only their combined evidence materialization exceeds the allowance.
            foreach (var layer in layers)
                Assert.Single(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Sentinel", Layers: new HashSet<MemoryLayer> { layer })));
        }
        var error = await Assert.ThrowsAsync<MemorySearchBudgetExceededException>(() =>
            store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Sentinel", Layers: layers)));
        Assert.Equal("payload", error.Budget);
        Assert.Contains("Narrow the query", error.Message);
        Assert.DoesNotContain(body, error.Message);
        Assert.Empty(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "absent", Layers: layers)));
    }
}
