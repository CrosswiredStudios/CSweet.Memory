using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    // A trusted-storage/corrupted-import case: an independently resealed correction is self-consistent,
    // but its contributor certificate no longer verifies and it omits its inherited shared audience.
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExportHidesResealedCorrectionsWhoseContributorCertificateDoesNotVerify(string provider)
    {
        foreach (var defect in new[] { "missing-contributor", "wrong-fingerprint", "omitted-audience" })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var target = TransferNamespace("new");
            var shared = EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test");
            await TransferSource(engine, shared);
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [shared], target, TransferAccess, "memory shared notes"));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, applied.AppliedEpisodeId!.Value))!;
            var corrected = await Correction(store, target, [copy]);
            var now = DateTimeOffset.UtcNow;
            var entity = new MemoryEntity(Guid.NewGuid(), target.Partition, "person", "memory subject", [], null, false, now, now)
                { SourceEpisodeIds = [corrected.Id], Sensitivity = MemorySensitivity.Personal };
            await store.UpsertEntityAsync(entity);
            var claim = new MemoryClaim(Guid.NewGuid(), target.Partition, corrected.Id, entity.Id, "prefers", null, "memory corrected",
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Personal, 1, 1, now, null, now)
                { SourceEpisodeIds = [corrected.Id] };
            await store.WriteClaimAsync(claim);
            await store.WriteProcedureAsync(new(Guid.NewGuid(), target.Partition, corrected.Id, "memory", "memory corrected", null, 1,
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now, null, now) { SourceEpisodeIds = [corrected.Id] });

            var allowed = TransferEngine(store);
            var before = await allowed.ExportAsync(target.Partition, TransferAccess);
            Assert.Contains(before.Episodes, x => x.Id == corrected.Id);
            Assert.Contains(before.Claims, x => x.Id == claim.Id);

            var evidence = corrected.CorrectionEvidence!;
            var damaged = MemorySourceIntegrity.Seal(defect switch
            {
                "missing-contributor" => corrected with { CorrectionEvidence = evidence with
                    { Sources = [new(Guid.NewGuid(), copy.SourceFingerprint!)], RequiredSharedPartitions = null } },
                "wrong-fingerprint" => corrected with { CorrectionEvidence = evidence with
                    { Sources = [new(copy.Id, "sha256-v1:" + new string('0', 64))], RequiredSharedPartitions = null } },
                _ => corrected with { CorrectionEvidence = evidence with { RequiredSharedPartitions = null } }
            });
            Assert.True(MemorySourceIntegrity.IsVerified(damaged));
            Assert.Null(MemorySharedAudiences.Required(damaged));
            await DropCorrectionFingerprintGuard(fixture);
            await ReplaceCorrectionPayload(fixture, corrected.Id, damaged);

            var denied = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()), authorizer: new SharedDeniedAuthorizer(shared.Partition));
            Assert.Empty((await denied.RecallAsync(new(target.Partition, target.Scope, "memory", Access: TransferAccess))).Items);
            foreach (var engineUnderTest in new[] { denied, allowed })
            {
                var export = await engineUnderTest.ExportAsync(target.Partition, TransferAccess);
                Assert.DoesNotContain(export.Episodes, x => x.Id == corrected.Id);
                Assert.DoesNotContain(export.Entities, x => x.Id == entity.Id);
                Assert.DoesNotContain(export.Claims, x => x.Id == claim.Id);
                Assert.DoesNotContain(export.Procedures, x => x.EpisodeId == corrected.Id);
            }
            Assert.DoesNotContain(await store.SearchAsync(new(target.Partition, target.Scope, "memory")), x => x.Id == corrected.Id);
        }
    }

    // Authorized inspection still sees an intact correction after its contributor is suppressed, expired or
    // revoked, while recall stays denied. Retained integrity is not recall eligibility.
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExportRetainsIntactCorrectionsAfterLifecycleChangesWithoutRestoringRecall(string provider)
    {
        foreach (var state in new[] { "suppressed", "expired", "revoked" })
        {
            await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
            var engine = TransferEngine(store); var target = TransferNamespace("new");
            var shared = EmployeeMemoryNamespaces.Team("transfer-tenant", Guid.NewGuid().ToString("D"), "test");
            await TransferSource(engine, shared);
            var draft = await engine.PrepareKnowledgeTransferAsync(new("old", "new", [shared], target, TransferAccess, "memory notes",
                Layers: new HashSet<MemoryLayer>()));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
            var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
            var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, applied.AppliedEpisodeId!.Value))!;
            var corrected = await Correction(store, target, [copy]);
            if (state == "suppressed") await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(target.Partition, copy.Id);
            else if (state == "expired") await fixture.Sql(fixture.Postgres
                ? $"UPDATE csweet_memory_episodes SET expires_at='2000-01-01',payload=jsonb_set(payload,ARRAY['expiresAt'],'\"2000-01-01T00:00:00Z\"'::jsonb) WHERE id='{copy.Id:D}'"
                : $"UPDATE memory_episodes SET expires_at='2000-01-01T00:00:00Z',payload=json_set(payload,'$.expiresAt','2000-01-01T00:00:00Z') WHERE id='{copy.Id:D}'");
            else await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(applied with { Status = KnowledgeTransferStatus.Rejected });

            var loaded = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, corrected.Id))!;
            Assert.False(MemoryProvenance.IsCurrent(loaded, target.Partition, corrected.Id, DateTimeOffset.UtcNow));
            Assert.Empty((await engine.RecallAsync(new(target.Partition, target.Scope, "memory", Access: TransferAccess))).Items);
            Assert.Contains((await engine.ExportAsync(target.Partition, TransferAccess)).Episodes, x => x.Id == corrected.Id);
            var denied = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()), authorizer: new SharedDeniedAuthorizer(shared.Partition));
            Assert.DoesNotContain((await denied.ExportAsync(target.Partition, TransferAccess)).Episodes, x => x.Id == corrected.Id);
        }
    }

    // Derived validation must not survive an evidence mutation: a loaded, verified copy that is
    // changed and resealed by a caller is unverified until a store resolves it again.
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ResealingALoadedVerifiedEpisodeClearsItsDerivedVerification(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var original = await TransferSource(engine, source);
        var package = await ApplyTransfer(engine, source, target);
        var reader = (IMemorySourceReader)store; var now = DateTimeOffset.UtcNow;
        var copy = (await reader.GetEpisodeAsync(target.Partition, package.AppliedEpisodeId!.Value))!;
        Assert.True(MemoryProvenance.IsCurrent(copy, target.Partition, copy.Id, now));
        var corrected = await Correction(store, target, [await reader.GetEpisodeAsync(target.Partition, copy.Id) ?? copy]);
        now = DateTimeOffset.UtcNow;
        Assert.True(MemoryProvenance.IsCurrent(corrected, target.Partition, corrected.Id, now));

        var changedCopy = MemorySourceIntegrity.Seal(copy with
            { TransferEvidence = copy.TransferEvidence! with { Records = [], RequiredSharedPartitions = null } });
        Assert.True(MemorySourceIntegrity.IsVerified(changedCopy));
        Assert.False(MemoryProvenance.IsCurrent(changedCopy, target.Partition, copy.Id, now));
        var changedCorrection = MemorySourceIntegrity.Seal(corrected with
            { CorrectionEvidence = corrected.CorrectionEvidence! with { Sources = [new(Guid.NewGuid(), original.SourceFingerprint!)] } });
        Assert.True(MemorySourceIntegrity.IsVerified(changedCorrection));
        Assert.False(MemoryProvenance.IsCurrent(changedCorrection, target.Partition, corrected.Id, now));
        // Even an identical reseal is a new evidence assertion and requires resolution by a store.
        Assert.False(MemoryProvenance.IsCurrent(MemorySourceIntegrity.Seal(copy), target.Partition, copy.Id, now));
        // Ordinary evidence without ancestry needs no store certificate.
        Assert.True(MemoryProvenance.IsCurrent(MemorySourceIntegrity.Seal(original), source.Partition, original.Id, now));
        // The stores still resolve the unchanged persisted records as current.
        Assert.True(MemoryProvenance.IsCurrent(await reader.GetEpisodeAsync(target.Partition, copy.Id), target.Partition, copy.Id, now));
        Assert.True(MemoryProvenance.IsCurrent(await reader.GetEpisodeAsync(target.Partition, corrected.Id), target.Partition, corrected.Id, now));
    }
}
