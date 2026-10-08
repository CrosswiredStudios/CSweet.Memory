using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SharedCorrectionsAndNestedCopiesPreserveAudienceForEveryMemoryLayer(string provider)
    {
        foreach (var role in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var audience = Guid.NewGuid().ToString("D");
            var shared = role ? EmployeeMemoryNamespaces.Role("transfer-tenant", audience, "test") : EmployeeMemoryNamespaces.Team("transfer-tenant", audience, "test");
            var target = TransferNamespace("new"); var third = TransferNamespace("third");
            await TransferSource(engine, shared);
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [shared], target, TransferAccess, "memory shared notes"));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, applied.AppliedEpisodeId!.Value))!;
            var corrected = await Correction(store, target, [copy]);
            Assert.StartsWith("sha256-v3:", corrected.SourceFingerprint);
            Assert.True(MemoryProvenance.IsCurrent(corrected, target.Partition, corrected.Id, DateTimeOffset.UtcNow));
            Assert.Equal(shared.Partition, Assert.Single(corrected.CorrectionEvidence!.RequiredSharedPartitions!));
            var now = DateTimeOffset.UtcNow;
            var entity = new MemoryEntity(Guid.NewGuid(), target.Partition, "person", "memory subject", [], null, false, now, now)
                { SourceEpisodeIds = [corrected.Id], Sensitivity = MemorySensitivity.Personal };
            await store.UpsertEntityAsync(entity);
            var claim = new MemoryClaim(Guid.NewGuid(), target.Partition, corrected.Id, entity.Id, "prefers", null, "memory corrected",
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Personal, 1, 1, now, null, now)
                { SourceEpisodeIds = [corrected.Id] };
            await store.WriteClaimAsync(claim);
            await store.WriteBlockAsync(new(Guid.NewGuid(), target.Partition, "memory", "memory corrected", 1, 200, true, MemoryTrustTier.ConfirmedUser, now)
                { SourceEpisodeIds = [corrected.Id], Sensitivity = MemorySensitivity.Personal, Confirmation = MemoryConfirmationState.Confirmed });
            await store.WriteProcedureAsync(new(Guid.NewGuid(), target.Partition, corrected.Id, "memory", "memory corrected", null, 1,
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now, null, now) { SourceEpisodeIds = [corrected.Id] });
            var candidates = await store.SearchAsync(new(target.Partition, target.Scope, "memory"));
            foreach (var layer in new[] { MemoryLayer.Episodic, MemoryLayer.Semantic, MemoryLayer.Core, MemoryLayer.Procedural })
                Assert.Contains(candidates, x => x.Layer == layer && x.RequiredSharedPartitions?.Contains(shared.Partition) == true);
            var denied = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()), authorizer: new SharedDeniedAuthorizer(shared.Partition));
            Assert.Empty((await denied.RecallAsync(new(target.Partition, target.Scope, "memory", Access: TransferAccess))).Items);
            var hidden = await denied.ExportAsync(target.Partition, TransferAccess);
            Assert.Empty(hidden.Episodes); Assert.Empty(hidden.Claims); Assert.Empty(hidden.Entities); Assert.Empty(hidden.Blocks); Assert.Empty(hidden.Procedures);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ConfirmClaimAsync(claim.Id, true, TransferAccess));
            var nested = await ApplyTransfer(engine, target, third);
            Assert.Equal(shared.Partition, Assert.Single(nested.ApprovedEvidence!.RequiredSharedPartitions!));
            Assert.Contains(nested.ApprovedEvidence.Records, x => x.Id == corrected.Id);
            Assert.Contains(nested.ApprovedEvidence.Records, x => x.Id == copy.Id);
            Assert.Single(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
            Assert.Empty((await denied.RecallAsync(new(third.Partition, third.Scope, "memory", Access: TransferAccess))).Items);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CorrectionTamperingCannotDetachAncestryOrSharedAuthority(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var target = TransferNamespace("new");
        var source = await TransferSource(engine, target); var corrected = await Correction(store, target, [source]);
        Assert.DoesNotContain("CorrectionEvidence", JsonSerializer.Serialize(source));
        Assert.StartsWith("sha256-v1:", source.SourceFingerprint);
        // First verify the production guard. Then simulate a damaged imported database
        // in this isolated fixture so independently resealed malformed ancestry is tested.
        await Assert.ThrowsAnyAsync<Exception>(() => ReplaceCorrectionPayload(fixture, corrected.Id, MemorySourceIntegrity.Seal(corrected with { Content = "changed" })));
        await DropCorrectionFingerprintGuard(fixture);
        foreach (var defect in new[] { "stripped", "empty", "wrong-fingerprint", "wrong-operation", "duplicate", "foreign-partition", "cycle", "audience", "ambiguous" })
        {
            var evidence = corrected.CorrectionEvidence!;
            var changed = defect switch
            {
                "stripped" => corrected with { CorrectionEvidence = null },
                "empty" => corrected with { CorrectionEvidence = evidence with { Sources = [] } },
                "wrong-fingerprint" => corrected with { CorrectionEvidence = evidence with { Sources = [new(source.Id, "sha256-v1:" + new string('0', 64))] } },
                "wrong-operation" => corrected with { CorrectionEvidence = evidence with { ReviewOperationId = Guid.NewGuid() } },
                "duplicate" => corrected with { CorrectionEvidence = evidence with { Sources = [evidence.Sources[0], evidence.Sources[0]] } },
                "foreign-partition" => corrected with { Partition = TransferNamespace("foreign").Partition },
                "cycle" => corrected with { CorrectionEvidence = evidence with { Sources = [new(corrected.Id, corrected.SourceFingerprint!)] } },
                "ambiguous" => corrected with { TransferEvidence = new(Guid.NewGuid(), "invalid", []) },
                _ => corrected with { CorrectionEvidence = evidence with { RequiredSharedPartitions = [EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test").Partition] } }
            };
            Assert.False(MemorySourceIntegrity.IsVerified(changed));
            if (defect != "stripped" && defect != "ambiguous") changed = MemorySourceIntegrity.Seal(changed);
            if (defect == "foreign-partition")
            {
                await Assert.ThrowsAnyAsync<Exception>(() => ReplaceCorrectionPayload(fixture, corrected.Id, changed));
                continue;
            }
            await ReplaceCorrectionPayload(fixture, corrected.Id, changed);
            var loaded = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, corrected.Id))!;
            Assert.False(MemoryProvenance.IsCurrent(loaded, target.Partition, corrected.Id, DateTimeOffset.UtcNow));
            Assert.DoesNotContain(await store.SearchAsync(new(target.Partition, target.Scope, "memory")), x => x.Id == corrected.Id);
            Assert.Equal("memory_erasure_lineage_review_required", (await ((IMemoryErasureStore)store).PreviewEpisodeErasureAsync(target.Partition, source.Id)).BlockedReason);
        }
        await ReplaceCorrectionPayload(fixture, corrected.Id, corrected);
        Assert.True(MemoryProvenance.IsCurrent(await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, corrected.Id), target.Partition, corrected.Id, DateTimeOffset.UtcNow));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CorrectionsWithholdEveryDescendantAfterSourceLifecycleChangeAndStillPermitVerifiedErasure(string provider)
    {
        foreach (var state in new[] { "suppressed", "expired", "revoked" })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var target = TransferNamespace("new"); var third = TransferNamespace("third");
            var shared = EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test");
            var independent = await TransferSource(engine, shared);
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [shared], target, TransferAccess, "memory notes", Layers: new HashSet<MemoryLayer>()));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true)); var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, applied.AppliedEpisodeId!.Value))!;
            var corrected = await Correction(store, target, [copy]); var nested = await ApplyTransfer(engine, target, third);
            if (state == "suppressed") await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(target.Partition, copy.Id);
            else if (state == "expired") await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET expires_at='2000-01-01',payload=jsonb_set(payload,ARRAY['expiresAt'],'\"2000-01-01T00:00:00Z\"'::jsonb) WHERE id='{copy.Id:D}'"
                : $"UPDATE memory_episodes SET expires_at='2000-01-01T00:00:00Z',payload=json_set(payload,'$.expiresAt','2000-01-01T00:00:00Z') WHERE id='{copy.Id:D}'");
            else await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Status = KnowledgeTransferStatus.Rejected });
            Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory"))); Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
            var erasure = (IMemoryErasureStore)store;
            var preview = await erasure.PreviewEpisodeErasureAsync(target.Partition, copy.Id); Assert.Null(preview.BlockedReason);
            Assert.Contains(preview.Targets, x => x.Id == corrected.Id); Assert.Contains(preview.Targets, x => x.Id == nested.AppliedEpisodeId);
            await erasure.EraseEpisodeAsync(target.Partition, copy.Id, preview.EvidenceToken);
            Assert.Empty((await store.ExportAsync(target.Partition)).Episodes); Assert.Empty((await store.ExportAsync(third.Partition)).Episodes);
            Assert.Contains((await store.ExportAsync(shared.Partition)).Episodes, x => x.Id == independent.Id);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CorrectionsBindSourceFingerprintAndCurrentClassificationAndRetention(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var target = TransferNamespace("new"); var original = await TransferSource(TransferEngine(store), target);
        var corrected = await Correction(store, target, [original]); var reader = (IMemorySourceReader)store;
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id='{original.Id:D}'"
            : $"UPDATE memory_episodes SET legal_hold=1,payload=json_set(payload,'$.legalHold',json('true')) WHERE id='{original.Id:D}'");
        Assert.True(MemoryProvenance.IsCurrent(await reader.GetEpisodeAsync(target.Partition, corrected.Id), target.Partition, corrected.Id, DateTimeOffset.UtcNow));
        Assert.Equal("memory_legal_hold_prevents_deletion", (await ((IMemoryErasureStore)store).PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['sensitivity'],'4'::jsonb) WHERE id='{original.Id:D}'"
            : $"UPDATE memory_episodes SET payload=json_set(payload,'$.sensitivity',4) WHERE id='{original.Id:D}'");
        Assert.False(MemoryProvenance.IsCurrent(await reader.GetEpisodeAsync(target.Partition, corrected.Id), target.Partition, corrected.Id, DateTimeOffset.UtcNow));
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'false'::jsonb) WHERE id='{original.Id:D}'"
            : $"UPDATE memory_episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{original.Id:D}'");
        await Assert.ThrowsAnyAsync<Exception>(() => ReplaceCorrectionPayload(fixture, original.Id, MemorySourceIntegrity.Seal(original with { Content = "memory different evidence" })));
        await DropCorrectionFingerprintGuard(fixture);
        await ReplaceCorrectionPayload(fixture, original.Id, MemorySourceIntegrity.Seal(original with { Content = "memory different evidence" }));
        Assert.False(MemoryProvenance.IsCurrent(await reader.GetEpisodeAsync(target.Partition, corrected.Id), target.Partition, corrected.Id, DateTimeOffset.UtcNow));
        Assert.Equal("memory_erasure_lineage_review_required", (await ((IMemoryErasureStore)store).PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CorrectionRetentionChecksLiveSharedHoldsAndTheExactApprovedHistoricalEvidence(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var target = TransferNamespace("new");
        var shared = EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test");
        var original = await TransferSource(engine, shared);
        var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [shared], target, TransferAccess, "memory notes",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
        await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true)); var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
        var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, applied.AppliedEpisodeId!.Value))!;
        var corrected = await Correction(store, target, [copy]); var erasure = (IMemoryErasureStore)store;
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'true'::jsonb) WHERE id='{original.Id:D}'"
            : $"UPDATE memory_episodes SET legal_hold=1,payload=json_set(payload,'$.legalHold',json('true')) WHERE id='{original.Id:D}'");
        Assert.Equal("memory_legal_hold_prevents_deletion", (await erasure.PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
        var deletion = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
        Assert.Equal("memory_legal_hold_prevents_deletion", deletion.Message);
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],'false'::jsonb) WHERE id='{original.Id:D}'"
            : $"UPDATE memory_episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{original.Id:D}'");
        Assert.Null((await erasure.PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Debrief = "different certificate" });
        Assert.Equal("memory_erasure_lineage_review_required", (await erasure.PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied);
        await DropCorrectionFingerprintGuard(fixture);
        await ReplaceCorrectionPayload(fixture, original.Id, MemorySourceIntegrity.Seal(original with { Content = "memory rewritten original" }));
        Assert.Equal("memory_erasure_lineage_review_required", (await erasure.PreviewEpisodeErasureAsync(target.Partition, corrected.Id)).BlockedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CorrectionCaptureRejectsMissingSourcesOverflowAndUnreadableAncestry(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store(); var target = TransferNamespace("new");
        var capture = (IMemoryCorrectionEvidenceStore)store;
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureCorrectionEvidenceAsync(target.Partition, Guid.NewGuid(), []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureCorrectionEvidenceAsync(target.Partition, Guid.NewGuid(), [Guid.NewGuid()]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => capture.CaptureCorrectionEvidenceAsync(target.Partition, Guid.NewGuid(), Enumerable.Range(0, 129).Select(_ => Guid.NewGuid()).ToArray()));
        var current = await TransferSource(TransferEngine(store), target);
        for (var i = 0; i < 3; i++) current = await Correction(store, target, [current]);
        Assert.True(MemoryProvenance.IsCurrent(current, target.Partition, current.Id, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureCorrectionEvidenceAsync(target.Partition, Guid.NewGuid(), [current.Id]));
    }

    private static async Task<MemoryEpisode> Correction(IMemoryStore store, MemoryNamespace target, IReadOnlyList<MemoryEpisode> sources)
    {
        var operation = Guid.NewGuid(); var evidence = await ((IMemoryCorrectionEvidenceStore)store).CaptureCorrectionEvidenceAsync(target.Partition, operation, sources.Select(x => x.Id).ToArray());
        var now = DateTimeOffset.UtcNow;
        var corrected = new MemoryEpisode(Guid.NewGuid(), target.Partition, target.Scope, "memory corrected checklist", "text/plain", new("user", operation.ToString("D"), "human"), "checksum", now, now,
            ExpiresAt: sources.Select(x => x.ExpiresAt).Where(x => x.HasValue).Min(), LegalHold: sources.Any(x => x.LegalHold), Sensitivity: sources.Max(x => x.Sensitivity)) { CorrectionEvidence = evidence };
        await store.AppendEpisodeAsync(corrected);
        return (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, corrected.Id))!;
    }

    private static Task ReplaceCorrectionPayload(Fixture fixture, Guid id, MemoryEpisode episode)
    {
        var payload = JsonSerializer.Serialize(episode, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace("'", "''");
        return fixture.Sql($"UPDATE {fixture.Prefix}episodes SET payload='{payload}'{(fixture.Postgres ? "::jsonb" : "")} WHERE id='{id:D}'");
    }

    private static Task DropCorrectionFingerprintGuard(Fixture fixture) => fixture.Sql(fixture.Postgres
        ? $"DROP TRIGGER {fixture.Prefix}source_fingerprint_guard ON {fixture.Prefix}episodes"
        : $"DROP TRIGGER {fixture.Prefix}source_fingerprint_guard");
}
