using System.Data.Common;
using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SuppressionWithholdsEveryChannelAndLateDerivativesButRetainsHeldEvidence(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var now = DateTimeOffset.UtcNow;
        var source = fixture.Episode() with { Content = "Meridian evidence", LegalHold = true, Sensitivity = MemorySensitivity.Personal };
        await store.AppendEpisodeAsync(source);
        var duplicate = source with { Id = Guid.NewGuid(), Source = source.Source with { Type = "USER" } };
        await store.AppendEpisodeAsync(duplicate);
        var entity = fixture.Entity(source) with { CanonicalName = "Meridian" };
        await store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "uses", null, "Meridian",
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Personal, 1, 1, source.OccurredAt, null, now);
        await store.WriteClaimAsync(claim);
        await store.WriteEdgeAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "links", entity.Id,
            MemoryTrustTier.UnconfirmedUser, 1, source.OccurredAt, null, false, now));
        await store.WriteProcedureAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, "Meridian", "Meridian procedure", null, 1,
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.Confirmed, source.OccurredAt, null, now));
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Meridian", "Meridian summary", 1, 100, true,
            MemoryTrustTier.UnconfirmedUser, source.OccurredAt) { SourceEpisodeIds = [source.Id], Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(block);
        await store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, MemoryLayer.Episodic, [1f, 0f], "test", now));
        var before = await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Meridian", Embedding: [1f, 0f]));
        foreach (var channel in new[] { "fulltext", "semantic", "graph", "procedure", "core", "vector" })
            Assert.Contains(before, x => x.RetrievalChannel == channel);
        var original = (await ((IMemorySourceReader)store).GetEpisodeAsync(fixture.Partition, source.Id))!;
        await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(fixture.Partition, source.Id);
        await store.WriteBlockAsync(block with { Content = "Meridian late summary", Revision = 2 });
        Assert.Empty(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Meridian", AsOf: now, Embedding: [1f, 0f])));
        Assert.Null(await store.FindEntityAsync(fixture.Partition, entity.CanonicalName));
        var retained = (await store.ExportAsync(fixture.Partition)).Episodes;
        Assert.Equal(2, retained.Count);
        Assert.All(retained, x => { Assert.True(x.IsSuppressed); Assert.True(x.LegalHold); Assert.True(MemorySourceIntegrity.IsVerified(x)); });
        Assert.Equal(original.SourceFingerprint, retained.Single(x => x.Id == source.Id).SourceFingerprint);
        Assert.Empty(MemoryReadProjection.Create(await store.ExportAsync(fixture.Partition), fixture.Partition, MemorySensitivity.Restricted, now).Episodes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(fixture.Partition));
        var history = await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id);
        Assert.Equal(2, history.Items.Count);
        Assert.False(JsonSerializer.Deserialize<MemoryEpisode>(history.Items[0].PayloadJson, JsonOptions)!.IsSuppressed);
        Assert.True(JsonSerializer.Deserialize<MemoryEpisode>(history.Items[1].PayloadJson, JsonOptions)!.IsSuppressed);
        await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(fixture.Partition, source.Id);
        Assert.Equal(2, (await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SuppressionSurvivesPurgeRestartAndChangedEpisodeIdsWithinItsPartition(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(fixture.Partition, source.Id);
        await Assert.ThrowsAnyAsync<DbException>(() => fixture.Sql(fixture.Postgres
            ? "UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{isSuppressed}','false')"
            : "UPDATE memory_episodes SET payload=json_set(payload,'$.isSuppressed',json('false'))"));
        await store.DeleteScopeAsync(fixture.Partition);
        await using var restarted = fixture.Store();
        await ((IMemorySuppressionStore)restarted).SuppressEpisodeAsync(fixture.Partition, source.Id);
        foreach (var replay in new[] { source, source with { Id = Guid.NewGuid() }, source with { Source = new("user", "changed") } })
            await Assert.ThrowsAnyAsync<DbException>(() => restarted.AppendEpisodeAsync(replay));
        var other = source with { Id = Guid.NewGuid(), Partition = fixture.Partition with { AgentId = "other" } };
        await restarted.AppendEpisodeAsync(other);
        Assert.Single(await restarted.SearchAsync(new(other.Partition, MemoryScope.Agent, "evidence")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ((IMemorySuppressionStore)restarted).SuppressEpisodeAsync(other.Partition, source.Id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SuppressionInvalidatesChainedTransfersAndTheirCurrentApprovals(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source); await ApplyTransfer(engine, source, target);
        var third = TransferNamespace("third"); await ApplyTransfer(engine, target, third);
        var draft = await PrepareTransfer(engine, source, TransferNamespace("pending"));
        await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
        await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(source.Partition, episode.Id);
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
    }
}
