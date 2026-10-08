using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NotesOnlyRetentionSurvivesSuppressionExpiryAndRevocationWithoutRestoringRecall(string provider)
    {
        foreach (var role in new[] { false, true })
        foreach (var state in new[] { "suppressed", "expired", "revoked" })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var audience = Guid.NewGuid().ToString("D");
            var source = role ? EmployeeMemoryNamespaces.Role("transfer-tenant", audience, "test") : EmployeeMemoryNamespaces.Team("transfer-tenant", audience, "test");
            var original = await TransferSource(engine, source); var target = TransferNamespace("new"); var third = TransferNamespace("third");
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [source], target, TransferAccess,
                "memory shared notes", Layers: new HashSet<MemoryLayer>()));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var nested = await ApplyTransfer(engine, target, third); var id = applied.AppliedEpisodeId!.Value;
            if (state == "suppressed") await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(target.Partition, id);
            else if (state == "expired") await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET expires_at='2000-01-01',payload=jsonb_set(payload,ARRAY['expiresAt'],'\"2000-01-01T00:00:00Z\"'::jsonb) WHERE id='{id:D}'"
                : $"UPDATE memory_episodes SET expires_at='2000-01-01T00:00:00Z',payload=json_set(payload,'$.expiresAt','2000-01-01T00:00:00Z') WHERE id='{id:D}'");
            else await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Status = KnowledgeTransferStatus.Rejected });
            Assert.Empty((await engine.RecallAsync(new(target.Partition, target.Scope, "memory", Access: TransferAccess))).Items);
            Assert.Empty((await engine.RecallAsync(new(third.Partition, third.Scope, "memory", Access: TransferAccess))).Items);
            var erasure = (IMemoryErasureStore)store;
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Debrief = "changed" });
            Assert.Equal("memory_erasure_lineage_review_required", (await erasure.PreviewEpisodeErasureAsync(target.Partition, id)).BlockedReason);
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Status = state == "revoked" ? KnowledgeTransferStatus.Rejected : KnowledgeTransferStatus.Applied });
            await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id='{id:D}'"
                : $"UPDATE memory_episodes SET legal_hold=1,payload=json_set(payload,'$.legalHold',json('true')) WHERE id='{id:D}'");
            Assert.Equal("memory_legal_hold_prevents_deletion", (await erasure.PreviewEpisodeErasureAsync(target.Partition, id)).BlockedReason);
            await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'false'::jsonb) WHERE id='{id:D}'"
                : $"UPDATE memory_episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{id:D}'");
            var preview = await erasure.PreviewEpisodeErasureAsync(target.Partition, id); Assert.Null(preview.BlockedReason);
            Assert.Contains(preview.Targets, x => x.Kind == MemoryErasureKind.Episode && x.Id == nested.AppliedEpisodeId);
            await erasure.EraseEpisodeAsync(target.Partition, id, preview.EvidenceToken);
            Assert.Empty((await store.ExportAsync(target.Partition)).Episodes); Assert.Empty((await store.ExportAsync(third.Partition)).Episodes);
            Assert.Contains((await store.ExportAsync(source.Partition)).Episodes, x => x.Id == original.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SharedNotesOnlyErasureRequiresAnExactLiveTransferCertificate(string provider)
    {
        foreach (var role in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var id = Guid.NewGuid().ToString("D");
            var source = role ? EmployeeMemoryNamespaces.Role("transfer-tenant", id, "test") : EmployeeMemoryNamespaces.Team("transfer-tenant", id, "test");
            var original = await TransferSource(engine, source); var target = TransferNamespace("new");
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [source], target, TransferAccess,
                "memory shared notes", Layers: new HashSet<MemoryLayer>()));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var erasure = (IMemoryErasureStore)store; var copy = applied.AppliedEpisodeId!.Value;
            Assert.Null((await erasure.PreviewEpisodeErasureAsync(target.Partition, copy)).BlockedReason);
            await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET payload=payload-'legalHold' WHERE id='{copy:D}'"
                : $"UPDATE memory_episodes SET payload=json_remove(payload,'$.legalHold') WHERE id='{copy:D}'");
            Assert.Equal("memory_erasure_lineage_review_required", (await erasure.PreviewEpisodeErasureAsync(target.Partition, copy)).BlockedReason);
            await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'false'::jsonb) WHERE id='{copy:D}'"
                : $"UPDATE memory_episodes SET payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{copy:D}'");
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Debrief = "changed package" });
            Assert.Equal("memory_erasure_lineage_review_required", (await erasure.PreviewEpisodeErasureAsync(target.Partition, copy)).BlockedReason);
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied);
            var preview = await erasure.PreviewEpisodeErasureAsync(target.Partition, copy);
            await erasure.EraseEpisodeAsync(target.Partition, copy, preview.EvidenceToken);
            Assert.Empty((await store.ExportAsync(target.Partition)).Episodes);
            Assert.Contains((await store.ExportAsync(source.Partition)).Episodes, x => x.Id == original.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SharedSourceCopiesAndNestedNotesRetainCanonicalRestrictions(string provider)
    {
        foreach (var role in new[] { false, true })
        foreach (var notesOnly in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var id = Guid.NewGuid().ToString("D");
            var source = role ? EmployeeMemoryNamespaces.Role("transfer-tenant", id, "test") : EmployeeMemoryNamespaces.Team("transfer-tenant", id, "test");
            var target = TransferNamespace("new"); var third = TransferNamespace("third");
            await TransferSource(engine, source);
            var prepared = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [source], target, TransferAccess,
                "memory shared notes", Layers: notesOnly ? new HashSet<MemoryLayer>() : new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
            await engine.ApproveKnowledgeTransferAsync(new(prepared.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(prepared.Id, TransferAccess));
            Assert.Equal(source.Partition, Assert.Single(applied.ApprovedEvidence!.RequiredSharedPartitions!));
            var nested = await ApplyTransfer(engine, target, third);
            Assert.Equal(source.Partition, Assert.Single(nested.ApprovedEvidence!.RequiredSharedPartitions!));
            var candidate = Assert.Single(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
            Assert.Equal(source.Partition, Assert.Single(candidate.RequiredSharedPartitions!));
            var authorizer = new SharedDeniedAuthorizer(source.Partition);
            var restricted = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()), authorizer: authorizer);
            Assert.Empty((await restricted.RecallAsync(new(third.Partition, third.Scope, "memory", Access: TransferAccess))).Items);
            Assert.Empty((await restricted.ExportAsync(third.Partition, TransferAccess)).Episodes);
            var copy = Assert.Single((await store.ExportAsync(third.Partition)).Episodes);
            var now = DateTimeOffset.UtcNow;
            var entity = new MemoryEntity(Guid.NewGuid(), third.Partition, "person", "Shared person", [], null, false, now, now)
                { SourceEpisodeIds = [copy.Id], Sensitivity = MemorySensitivity.Personal };
            await store.UpsertEntityAsync(entity);
            var claim = new MemoryClaim(Guid.NewGuid(), third.Partition, copy.Id, entity.Id, "prefers", null, "memory",
                MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now) { SourceEpisodeIds = [copy.Id] };
            await store.WriteClaimAsync(claim);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restricted.ConfirmClaimAsync(claim.Id, true, TransferAccess));
            Assert.True(MemorySourceIntegrity.IsVerified(copy));
            var malformed = copy with { TransferEvidence = copy.TransferEvidence! with { RequiredSharedPartitions = null } };
            Assert.False(MemorySourceIntegrity.IsVerified(malformed));
            var raw = await store.ExportAsync(third.Partition);
            Assert.Empty(MemoryAudienceProjection.Create(raw with { Episodes = [malformed] }, _ => true).Episodes);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SharedSourceCannotApproveAMismatchedNamespaceOrWidenASharedTarget(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store(); var engine = TransferEngine(store);
        var source = EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test");
        var target = TransferNamespace("new"); await TransferSource(engine, source);
        var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [source], target, TransferAccess, "memory notes"));
        var tampered = draft with { SourceNamespaces = [source with { AudienceId = Guid.NewGuid().ToString("D") }] };
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(tampered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true)));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PrepareKnowledgeTransferAsync(new("old", "new", [source], source, TransferAccess, "notes")));
    }

    [Fact]
    public void NativeTransferEvidenceKeepsItsOriginalWireShape()
    {
        var evidence = new MemoryTransferEvidence(Guid.NewGuid(), "fingerprint", []);
        Assert.DoesNotContain("RequiredSharedPartitions", System.Text.Json.JsonSerializer.Serialize(evidence));
        Assert.False(MemorySharedAudiences.IsCanonical(new("tenant", "test", CustomNamespace: "team:not-canonical")));
        Assert.False(MemorySharedAudiences.IsCanonical(new("tenant", "test", ConversationId: "extra", CustomNamespace: "team:" + Guid.NewGuid().ToString("D"))));
    }

    private sealed class SharedDeniedAuthorizer(MemoryPartition denied) : IMemoryScopeAuthorizer
    {
        public ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken token = default) => ValueTask.FromResult(partition != denied);
        public ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken token = default) => ValueTask.FromResult(true);
    }
}
