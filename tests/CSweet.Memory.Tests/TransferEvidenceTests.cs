using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferAsOfHonorsExactSourceExpiryAndCurrentRevocationWithoutRewritingHistory(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var engine = TransferEngine(store);
        var source = TransferNamespace("old");
        var target = TransferNamespace("new");
        var third = TransferNamespace("third");
        var now = DateTimeOffset.UtcNow;
        var expiry = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero).AddHours(1).AddTicks(7)
            .ToOffset(TimeSpan.FromHours(-12));
        var original = await engine.IngestAsync(new(source.Partition, source.Scope, "memory exact expiry",
            new("user", Guid.NewGuid().ToString()), OccurredAt: now.AddHours(-1).ToOffset(TimeSpan.FromHours(14)),
            ExpiresAt: expiry, Access: TransferAccess, Sensitivity: MemorySensitivity.Personal));
        var first = await ApplyTransfer(engine, source, target);
        var second = await ApplyTransfer(engine, target, third);
        var reader = (IMemoryRevisionReader)store;
        var originalHistory = await reader.ReadRevisionsAsync(source.Partition, MemoryRecordKind.Episode, original.Id);
        var copyHistory = await reader.ReadRevisionsAsync(third.Partition, MemoryRecordKind.Episode, second.AppliedEpisodeId!.Value);
        foreach (var destination in new[] { target, third })
        {
            Assert.Single(await store.SearchAsync(new(destination.Partition, destination.Scope, "memory", AsOf: expiry.AddTicks(-1))));
            Assert.Empty(await store.SearchAsync(new(destination.Partition, destination.Scope, "memory", AsOf: expiry.ToOffset(TimeSpan.FromHours(14)))));
        }
        var copy = Assert.Single((await store.ExportAsync(third.Partition)).Episodes);
        Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory", AsOf: copy.OccurredAt.AddTicks(-1))));
        var availableAt = second.AppliedAt!.Value;
        Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory", AsOf: availableAt.AddTicks(-1))));
        Assert.Single(await store.SearchAsync(new(third.Partition, third.Scope, "memory", AsOf: availableAt)));
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(first with { Status = KnowledgeTransferStatus.Rejected });
        // AsOf is valid time. A current revocation must also withhold an earlier valid-time query.
        Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory", AsOf: availableAt)));
        var originalAfter = await reader.ReadRevisionsAsync(source.Partition, MemoryRecordKind.Episode, original.Id);
        var copyAfter = await reader.ReadRevisionsAsync(third.Partition, MemoryRecordKind.Episode, copy.Id);
        Assert.Equal(originalHistory.Items.Select(x => x.PayloadJson), originalAfter.Items.Select(x => x.PayloadJson));
        Assert.Equal(copyHistory.Items.Select(x => x.PayloadJson), copyAfter.Items.Select(x => x.PayloadJson));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RejectedCoreBlockCannotBeApprovedAgainOrKeepTransferredEvidenceEligible(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source);
        var block = new MemoryBlock(Guid.NewGuid(), source.Partition, "memory", "memory checklist", 1, 200, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow)
            { Confirmation = MemoryConfirmationState.Confirmed, SourceEpisodeIds = [episode.Id], Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(block);
        var package = await ApplyTransfer(engine, source, target, new HashSet<MemoryLayer> { MemoryLayer.Core });
        Assert.Single(package.Items);
        await store.WriteBlockAsync(block with { Confirmation = MemoryConfirmationState.Rejected, Revision = 2 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IMemoryTransferEvidenceStore)store).CaptureTransferEvidenceAsync(package));
        var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, package.AppliedEpisodeId!.Value))!;
        Assert.False(MemoryProvenance.IsCurrent(copy, target.Partition, copy.Id, DateTimeOffset.UtcNow));
    }

    private static readonly MemoryAccessContext TransferAccess = new(new("transfer-tenant", "reviewer"), "handoff", "review");
    private static MemoryNamespace TransferNamespace(string employee, string? user = null) => user is null
        ? EmployeeMemoryNamespaces.Employee("transfer-tenant", employee, "test")
        : new(new("transfer-tenant", "test", employee, user, CustomNamespace: $"relationship:{employee}:{user}"),
            MemoryScope.User, MemoryAudienceType.UserRelationship, user);
    private static MemoryEngine TransferEngine(IMemoryStore store) => new(store, Options.Create(new AgentMemoryOptions()),
        authorizer: new AllowAllMemoryScopeAuthorizer(), redactor: new PassthroughMemoryRedactor());
    private static async Task<KnowledgeTransferPackage> PrepareTransfer(MemoryEngine engine, MemoryNamespace source, MemoryNamespace target,
        IReadOnlySet<MemoryLayer>? layers = null) => await engine.PrepareKnowledgeTransferAsync(new(source.Partition.AgentId!,
            target.Partition.AgentId!, [source], target, TransferAccess, "Reviewed handoff", Layers: layers ?? new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
    private static async Task<MemoryEpisode> TransferSource(MemoryEngine engine, MemoryNamespace source, bool held = false) =>
        await engine.IngestAsync(new(source.Partition, source.Scope, "memory escalation checklist", new("user", Guid.NewGuid().ToString()),
            LegalHold: held, Access: TransferAccess, Sensitivity: MemorySensitivity.Personal));
    private static async Task<KnowledgeTransferPackage> ApplyTransfer(MemoryEngine engine, MemoryNamespace source, MemoryNamespace target,
        IReadOnlySet<MemoryLayer>? layers = null)
    {
        var package = await PrepareTransfer(engine, source, target, layers);
        await engine.ApproveKnowledgeTransferAsync(new(package.Id, TransferAccess, true));
        return await engine.ApplyKnowledgeTransferAsync(new(package.Id, TransferAccess));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferPinsFullClaimClosureAndWithholdsCopyAndDerivativesAfterReviewChanges(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store(); var engine = TransferEngine(store);
        var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var direct = await TransferSource(engine, source); var indirect = await TransferSource(engine, source);
        var entity = fixture.Entity(indirect) with { Partition = source.Partition, CanonicalName = "memory subject" };
        await store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), source.Partition, direct.Id, entity.Id, "prefers", null, "memory checklist",
            MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, MemorySensitivity.Personal, 1, 1,
            direct.OccurredAt, null, direct.RecordedAt) { SourceEpisodeIds = [direct.Id] };
        await store.WriteClaimAsync(claim);
        var package = await ApplyTransfer(engine, source, target, new HashSet<MemoryLayer> { MemoryLayer.Semantic });
        Assert.Single(package.Items);
        Assert.Equal(4, package.ApprovedEvidence!.Records.Count);
        Assert.Contains(package.ApprovedEvidence.Records, x => x.Id == indirect.Id && x.Kind == MemoryRecordKind.Episode);
        var copy = (await ((IMemorySourceReader)store).GetEpisodeAsync(target.Partition, package.AppliedEpisodeId!.Value))!;
        Assert.True(MemoryProvenance.IsCurrent(copy, target.Partition, copy.Id, DateTimeOffset.UtcNow));
        Assert.StartsWith("sha256-v2:", copy.SourceFingerprint);
        Assert.StartsWith("sha256-v1:", direct.SourceFingerprint);
        // Store verification is deliberately not transferable as a caller-controlled JSON flag.
        var wireCopy = JsonSerializer.Deserialize<MemoryEpisode>(JsonSerializer.Serialize(copy, JsonOptions), JsonOptions)!;
        Assert.False(MemoryProvenance.IsCurrent(wireCopy, target.Partition, copy.Id, DateTimeOffset.UtcNow));
        var block = new MemoryBlock(Guid.NewGuid(), target.Partition, "memory", "memory derived summary", 1, 200, true,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow) { SourceEpisodeIds = [copy.Id], Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(block);
        Assert.Equal(2, (await store.SearchAsync(new(target.Partition, target.Scope, "memory"))).Count);
        await store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected);
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        var projection = MemoryReadProjection.Create(await store.ExportAsync(target.Partition), target.Partition,
            MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
        Assert.Empty(projection.Episodes); Assert.Empty(projection.Blocks);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
        Assert.Equal("memory_transfer_retention_review_required", error.Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferApprovalCannotApplyAfterSourceRevisionChanges(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source);
        var package = await PrepareTransfer(engine, source, target);
        await engine.ApproveKnowledgeTransferAsync(new(package.Id, TransferAccess, true));
        await UpdateTransferSource(fixture, episode.Id, "legalHold", "true");
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyKnowledgeTransferAsync(new(package.Id, TransferAccess)));
        Assert.Empty((await store.ExportAsync(target.Partition)).Episodes);
        Assert.Equal(KnowledgeTransferStatus.Approved, (await ((IKnowledgeTransferStore)store).GetKnowledgeTransferAsync(package.Id))!.Status);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferPreservesRelationshipAudienceAndRejectsWidening(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old", "customer");
        await TransferSource(engine, source);
        foreach (var target in new[] { TransferNamespace("new"), TransferNamespace("new", "other") })
        {
            var denied = await PrepareTransfer(engine, source, target);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApproveKnowledgeTransferAsync(new(denied.Id, TransferAccess, true)));
            Assert.Empty((await store.ExportAsync(target.Partition)).Episodes);
        }
        var allowed = TransferNamespace("new", "customer");
        await ApplyTransfer(engine, source, allowed);
        Assert.Single(await store.SearchAsync(new(allowed.Partition, allowed.Scope, "memory")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferHoldsAndLaterSourceDeletionAreEnforced(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source, held: true);
        var package = await ApplyTransfer(engine, source, target);
        Assert.True(package.ApprovedEvidence!.LegalHold);
        Assert.True(Assert.Single((await store.ExportAsync(target.Partition)).Episodes).LegalHold);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
        // A later privileged source deletion still cannot leave the copy recallable.
        await fixture.Sql($"DELETE FROM {fixture.Prefix}episodes WHERE id='{episode.Id:D}'");
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferRevocationAndChainedEvidenceCannotBecomeIndependentCopies(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var third = TransferNamespace("third"); await TransferSource(engine, source);
        var package = await ApplyTransfer(engine, source, target);
        await ApplyTransfer(engine, target, third);
        Assert.Single(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(package with { Status = KnowledgeTransferStatus.Rejected });
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        Assert.Empty(await store.SearchAsync(new(third.Partition, third.Scope, "memory")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ValidUnheldTransferCanBeDeletedButLegacyTransferRequiresReview(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        await TransferSource(engine, source); await ApplyTransfer(engine, source, target);
        await store.DeleteScopeAsync(target.Partition);
        Assert.Empty((await store.ExportAsync(target.Partition)).Episodes);
        await engine.IngestAsync(new(target.Partition, target.Scope, "memory legacy transfer", new("knowledge-transfer", "legacy"), Access: TransferAccess));
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        Assert.Equal("memory_transfer_retention_review_required",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition))).Message);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferCapturesCoreProcedureAndGraphDependencies(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source);
        var entity = fixture.Entity(episode) with { Partition = source.Partition, CanonicalName = "memory subject" };
        await store.UpsertEntityAsync(entity);
        await store.WriteEdgeAsync(new(Guid.NewGuid(), source.Partition, episode.Id, entity.Id, "knows", entity.Id,
            MemoryTrustTier.AgentInference, 1, episode.OccurredAt, null, true, episode.RecordedAt));
        await store.WriteBlockAsync(new(Guid.NewGuid(), source.Partition, "memory", "memory core", 1, 100, true,
            MemoryTrustTier.AgentInference, episode.RecordedAt) { SourceEpisodeIds = [episode.Id], Sensitivity = MemorySensitivity.Personal });
        await store.WriteProcedureAsync(new(Guid.NewGuid(), source.Partition, episode.Id, "memory", "memory procedure", null,
            1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed, episode.OccurredAt, null, episode.RecordedAt));
        var package = await ApplyTransfer(engine, source, target, new HashSet<MemoryLayer> { MemoryLayer.Core, MemoryLayer.Semantic, MemoryLayer.Procedural });
        Assert.Equal(3, package.Items.Count); Assert.Equal(5, package.ApprovedEvidence!.Records.Count);
        Assert.Single(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        await store.UpsertEntityAsync(entity with { CanonicalName = "changed name" });
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferCannotOutliveSourceExpiryOrLaterHold(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source); await ApplyTransfer(engine, source, target);
        await UpdateTransferSource(fixture, episode.Id, "expiresAt", JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        Assert.Equal("memory_transfer_retention_review_required",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition))).Message);
        // Restoring policy still advances its revision: previous approval never silently revives.
        await UpdateTransferSource(fixture, episode.Id, "expiresAt", "null");
        await UpdateTransferSource(fixture, episode.Id, "legalHold", "true");
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScopeAsync(target.Partition));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferManifestFingerprintCannotBeRewrittenAndPackageMutationWithholdsCopy(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        await TransferSource(engine, source); var package = await ApplyTransfer(engine, source, target);
        var copy = Assert.Single((await store.ExportAsync(target.Partition)).Episodes);
        await UpdateTransferSource(fixture, copy.Id, "transferEvidence", "null");
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => UpdateTransferSource(fixture, copy.Id, "sourceFingerprint", "null"));
        var secondTarget = TransferNamespace("second"); var second = await ApplyTransfer(engine, source, secondTarget);
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(second with { Debrief = "changed after approval" });
        Assert.Empty(await store.SearchAsync(new(secondTarget.Partition, secondTarget.Scope, "memory")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferApprovalRejectsCrossApplicationAndMisattributedSourceNamespaces(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        await TransferSource(engine, source); var draft = await PrepareTransfer(engine, source, target);
        foreach (var invalid in new[]
        {
            draft with { TargetNamespace = target with { Partition = target.Partition with { ApplicationId = "foreign" } } },
            draft with { SourceEmployeeId = "someone-else" },
            draft with { SourceNamespaces = [source with { Audience = MemoryAudienceType.Custom }] },
            draft with { Items = Enumerable.Repeat(draft.Items[0], 33).ToArray() }
        })
        {
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(invalid);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true)));
        }
        Assert.Empty((await store.ExportAsync(target.Partition)).Episodes);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferDepthLimitIsEnforcedBeforeCreatingAnUnreadableCopy(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("employee-0");
        await TransferSource(engine, source);
        for (var i = 1; i <= 3; i++)
        {
            var target = TransferNamespace($"employee-{i}");
            await ApplyTransfer(engine, source, target);
            Assert.Single(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
            source = target;
        }
        var fourth = TransferNamespace("employee-4"); var draft = await PrepareTransfer(engine, source, fourth);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true)));
        Assert.Empty((await store.ExportAsync(fourth.Partition)).Episodes);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TransferRetainsApprovedClassificationEvenWhenSourcesAreLessSensitive(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        await TransferSource(engine, source); var draft = await PrepareTransfer(engine, source, target);
        await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(draft with
        { Items = draft.Items.Select(x => x with { Sensitivity = MemorySensitivity.Confidential }).ToArray() });
        await engine.ApproveKnowledgeTransferAsync(new(draft.Id, TransferAccess, true));
        var applied = await engine.ApplyKnowledgeTransferAsync(new(draft.Id, TransferAccess));
        Assert.Equal(MemorySensitivity.Confidential, Assert.Single(await store.SearchAsync(new(target.Partition, target.Scope, "memory"))).Sensitivity);
        await UpdateTransferSource(fixture, applied.AppliedEpisodeId!.Value, "sensitivity", ((int)MemorySensitivity.Personal).ToString());
        Assert.Empty(await store.SearchAsync(new(target.Partition, target.Scope, "memory")));
    }

    private static Task UpdateTransferSource(Fixture fixture, Guid id, string field, string jsonValue) => fixture.Sql(fixture.Postgres
        ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{{field}}}','{jsonValue}'::jsonb) WHERE id='{id:D}'"
        : $"UPDATE {fixture.Prefix}episodes SET payload=json_set(payload,'$.{field}',json('{jsonValue}')) WHERE id='{id:D}'");
}
