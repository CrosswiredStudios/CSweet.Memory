using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed partial class ProvenanceStoreTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CoreReviewStateControlsSearchProjectionAndTransferSelection(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var episode = await fixture.EpisodeAsync();
        foreach (var state in new[] { MemoryConfirmationState.NotRequired, MemoryConfirmationState.Pending,
                     MemoryConfirmationState.Confirmed, MemoryConfirmationState.Rejected, (MemoryConfirmationState)99 })
        {
            var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, $"memory {state}", "memory core", 1, 500, true,
                MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow)
                { SourceEpisodeIds = [episode.Id], Sensitivity = MemorySensitivity.Internal, Confirmation = state };
            await fixture.Store.WriteBlockAsync(block);
            var ordinary = await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "memory", Layers: new HashSet<MemoryLayer> { MemoryLayer.Core }));
            var pending = await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "memory", IncludePending: true, Layers: new HashSet<MemoryLayer> { MemoryLayer.Core }));
            Assert.Equal(state is MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed, ordinary.Any(x => x.Id == block.Id));
            Assert.Equal(state is MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed or MemoryConfirmationState.Pending, pending.Any(x => x.Id == block.Id));
            if (pending.Any(x => x.Id == block.Id)) Assert.Equal(state, pending.Single(x => x.Id == block.Id).Confirmation);
            var raw = await fixture.Store.ExportAsync(fixture.Partition);
            Assert.Equal(state, raw.Blocks.Single(x => x.Id == block.Id).Confirmation);
            var projection = MemoryReadProjection.Create(raw, fixture.Partition, MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
            Assert.Equal(state is MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed,
                MemoryReadProjection.TransferItems(projection, fixture.Partition).Any(x => x.MemoryId == block.Id));
        }
    }

    [Fact]
    public void LegacyCorePayloadDefaultsToNotRequiredWithoutInventingSourceEvidence()
    {
        var block = new MemoryBlock(Guid.NewGuid(), new("tenant", "app"), "memory", "legacy", 1, 500, true, MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(block, options))!.AsObject();
        payload.Remove("confirmation");
        var restored = payload.Deserialize<MemoryBlock>(options)!;
        Assert.Equal(MemoryConfirmationState.NotRequired, restored.Confirmation);
        Assert.Equal(MemorySensitivity.Restricted, restored.Sensitivity); Assert.Empty(restored.SourceEpisodeIds);
    }
}
