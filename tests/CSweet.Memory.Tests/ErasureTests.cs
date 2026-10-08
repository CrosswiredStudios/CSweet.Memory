using System.Data.Common;
using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureRemovesEveryLinkedLayerHistoryAndCorrectionButRetainsOtherSources(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var other = fixture.Episode() with { Source = new("user", "independent") }; await store.AppendEpisodeAsync(other);
        var entity = fixture.Entity(source); await store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "prefers", null, "erase claim",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1,
            source.OccurredAt, null, source.RecordedAt);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "erase", "erase block", 1, 200, true,
            MemoryTrustTier.AgentInference, source.RecordedAt) { SourceEpisodeIds = [source.Id] };
        var edge = new MemoryEdge(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "erase", entity.Id,
            MemoryTrustTier.AgentInference, 1, source.OccurredAt, null, true, source.RecordedAt);
        var procedure = new ProceduralMemory(Guid.NewGuid(), fixture.Partition, source.Id, "erase", "erase procedure", null,
            1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, source.OccurredAt, null, source.RecordedAt);
        await store.WriteClaimAsync(claim); await store.WriteBlockAsync(block); await store.WriteBlockAsync(block with { Content = "new block", Revision = 2 });
        await store.WriteEdgeAsync(edge); await store.WriteProcedureAsync(procedure);
        await store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, claim.Id, MemoryLayer.Semantic, [1, 0], "test", source.RecordedAt));
        await store.RecordUseAsync(new(Guid.NewGuid(), fixture.Partition, "invocation", block.Id, MemoryLayer.Core, MemoryUseOutcome.Supplied, source.RecordedAt));
        var correction = fixture.Episode() with { Source = new("user", "correction"), OperationalReferences = [new("memory-block", block.Id.ToString("D"))] };
        await store.AppendEpisodeAsync(correction);
        var foreign = source with { Id = Guid.NewGuid(), Partition = fixture.Partition with { TenantId = "other-tenant" } };
        await store.AppendEpisodeAsync(foreign);
        var erase = (IMemoryErasureStore)store;
        var preview = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id);
        Assert.Null(preview.BlockedReason); Assert.Equal(9, preview.Targets.Count);
        Assert.DoesNotContain("source evidence", JsonSerializer.Serialize(preview));
        var result = await erase.EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken);
        Assert.Equal(9, result.ErasedRecords); Assert.True(result.ErasedRevisions >= 16);
        Assert.Equal(other.Id, Assert.Single((await store.ExportAsync(fixture.Partition)).Episodes).Id);
        Assert.Single((await store.ExportAsync(foreign.Partition)).Episodes);
        foreach (var target in preview.Targets.Where(x => (int)x.Kind < 7))
            Assert.Empty((await ((IMemoryRevisionReader)store).ReadRevisionsAsync(target.Partition, (MemoryRecordKind)target.Kind, target.Id)).Items);
        Assert.Empty(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "erase")));
        await using var restarted = fixture.Store();
        Assert.True((await ((IMemoryErasureStore)restarted).EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken)).WasReplay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IMemoryErasureStore)restarted).EraseEpisodeAsync(fixture.Partition, source.Id, new string('0', 64)));
        await Assert.ThrowsAnyAsync<DbException>(() => restarted.AppendEpisodeAsync(source with { Id = Guid.NewGuid(), Partition = fixture.Partition with { AgentId = "another-audience" } }));
        await Assert.ThrowsAnyAsync<DbException>(() => restarted.WriteBlockAsync(block with { Id = Guid.NewGuid(), Name = "replay" }));
        await Assert.ThrowsAnyAsync<DbException>(() => restarted.UpsertEntityAsync(entity with { Id = Guid.NewGuid(), ApplicationKey = "replay" }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureRefusesRootAndContributingHoldsThenRejectsStaleReleasePreview(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode(); var held = fixture.Episode() with { Source = new("user", "held-contributor"), LegalHold = true };
        await store.AppendEpisodeAsync(source); await store.AppendEpisodeAsync(held);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "shared", "retained", 1, 200, false, MemoryTrustTier.AgentInference, source.RecordedAt)
            { SourceEpisodeIds = [source.Id, held.Id] };
        await store.WriteBlockAsync(block);
        var erase = (IMemoryErasureStore)store;
        var blocked = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id);
        Assert.Equal("memory_legal_hold_prevents_deletion", blocked.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erase.EraseEpisodeAsync(fixture.Partition, source.Id, blocked.EvidenceToken));
        Assert.Equal(2, (await store.ExportAsync(fixture.Partition)).Episodes.Count);
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{legalHold}}','false') WHERE id='{held.Id:D}'"
            : $"UPDATE {fixture.Prefix}episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{held.Id:D}'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => erase.EraseEpisodeAsync(fixture.Partition, source.Id, blocked.EvidenceToken));
        var approved = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id); Assert.Null(approved.BlockedReason);
        await erase.EraseEpisodeAsync(fixture.Partition, source.Id, approved.EvidenceToken);
        Assert.Equal(held.Id, Assert.Single((await store.ExportAsync(fixture.Partition)).Episodes).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasurePurgesHistoricalOnlyEvidenceAndBlocksUnknownLegacyLineage(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var orphan = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "legacy", "unlinked", 1, 100, false, MemoryTrustTier.AgentInference, source.RecordedAt);
        await store.WriteBlockAsync(orphan);
        var erase = (IMemoryErasureStore)store;
        Assert.Equal("memory_erasure_lineage_review_required", (await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id)).BlockedReason);
        await fixture.Sql($"DELETE FROM {fixture.Prefix}blocks; DELETE FROM {fixture.Prefix}revisions WHERE kind=4; DELETE FROM {fixture.Prefix}episodes;");
        var preview = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id);
        var result = await erase.EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken);
        Assert.Equal(0, result.ErasedRecords); Assert.Equal(2, result.ErasedRevisions);
        await Assert.ThrowsAnyAsync<DbException>(() => store.AppendEpisodeAsync(source));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureReceiptFailureRollsBackPayloadHistoryAndFences(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        var erase = (IMemoryErasureStore)store; var preview = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id);
        await fixture.Sql(fixture.Postgres ? $"""
            CREATE FUNCTION fail_erasure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_erasure BEFORE INSERT ON {fixture.Prefix}erasure_receipts FOR EACH ROW EXECUTE FUNCTION fail_erasure();
            """ : $"CREATE TRIGGER fail_erasure BEFORE INSERT ON {fixture.Prefix}erasure_receipts BEGIN SELECT RAISE(ABORT,'injected_failure'); END;");
        await Assert.ThrowsAnyAsync<DbException>(() => erase.EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken));
        Assert.Single((await store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Single((await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items);
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}erased_records")));
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}erased_sources")));
        await fixture.Sql(fixture.Postgres ? $"DROP TRIGGER fail_erasure ON {fixture.Prefix}erasure_receipts; DROP FUNCTION fail_erasure();" : "DROP TRIGGER fail_erasure;");
        await erase.EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken);
        Assert.Empty((await store.ExportAsync(fixture.Partition)).Episodes);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureIncludesChainedTransfersPackagesAndHeldCopies(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source); var package = await ApplyTransfer(engine, source, target);
        var third = TransferNamespace("third"); await ApplyTransfer(engine, target, third);
        var erase = (IMemoryErasureStore)store;
        var preview = await erase.PreviewEpisodeErasureAsync(source.Partition, episode.Id);
        Assert.Null(preview.BlockedReason); Assert.Equal(2, preview.Targets.Count(x => x.Kind == MemoryErasureKind.Transfer));
        await erase.EraseEpisodeAsync(source.Partition, episode.Id, preview.EvidenceToken);
        Assert.Empty((await store.ExportAsync(target.Partition)).Episodes); Assert.Empty((await store.ExportAsync(third.Partition)).Episodes);
        Assert.Null(await ((IKnowledgeTransferStore)store).GetKnowledgeTransferAsync(package.Id));
        await Assert.ThrowsAnyAsync<DbException>(() => ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(package));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureKeepsCopiedHoldAfterOriginalHoldRelease(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var engine = TransferEngine(store); var source = TransferNamespace("old"); var target = TransferNamespace("new");
        var episode = await TransferSource(engine, source, held: true); await ApplyTransfer(engine, source, target);
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{legalHold}}','false') WHERE id='{episode.Id:D}'"
            : $"UPDATE {fixture.Prefix}episodes SET legal_hold=0,payload=json_set(payload,'$.legalHold',json('false')) WHERE id='{episode.Id:D}'");
        var erase = (IMemoryErasureStore)store; var preview = await erase.PreviewEpisodeErasureAsync(source.Partition, episode.Id);
        Assert.Equal("memory_legal_hold_prevents_deletion", preview.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erase.EraseEpisodeAsync(source.Partition, episode.Id, preview.EvidenceToken));
        Assert.Single((await store.ExportAsync(source.Partition)).Episodes); Assert.Single((await store.ExportAsync(target.Partition)).Episodes);
    }

    [Fact]
    public async Task ErasureHonorsSqliteIndexedHoldEvenWhenLegacyPayloadDisagrees()
    {
        await using var fixture = await Fixture.Create("sqlite"); await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        await fixture.Sql($"UPDATE {fixture.Prefix}episodes SET legal_hold=1 WHERE id='{source.Id:D}'");
        Assert.Equal("memory_legal_hold_prevents_deletion", (await ((IMemoryErasureStore)store).PreviewEpisodeErasureAsync(fixture.Partition, source.Id)).BlockedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasurePreservesEveryHistoricalLogicalSourceFence(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode(); await store.AppendEpisodeAsync(source);
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{source,id}}','\"changed-source\"') WHERE id='{source.Id:D}'"
            : $"UPDATE {fixture.Prefix}episodes SET payload=json_set(payload,'$.source.id','changed-source') WHERE id='{source.Id:D}'");
        var erase = (IMemoryErasureStore)store; var preview = await erase.PreviewEpisodeErasureAsync(fixture.Partition, source.Id);
        await erase.EraseEpisodeAsync(fixture.Partition, source.Id, preview.EvidenceToken);
        foreach (var identity in new[] { "test", "changed-source" })
            await Assert.ThrowsAnyAsync<DbException>(() => store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(),
                Partition = fixture.Partition with { AgentId = "new-audience" }, Source = new("user", identity) }));
        Assert.Equal(2L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}erased_sources")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureUpgradeIsAtomicAndPreservesPopulatedSources(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var original = fixture.Store();
        var source = fixture.Episode() with { LegalHold = true }; await original.AppendEpisodeAsync(source);
        var history = (await ((IMemoryRevisionReader)original).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items;
        foreach (var name in new[] { "episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings", "uses", "transfers" })
            await fixture.Sql(fixture.Postgres
                ? $"DROP TRIGGER {fixture.Prefix}{name}_erasure_guard ON {fixture.Prefix}{name}; DROP FUNCTION {fixture.Prefix}{name}_erasure_guard();"
                : $"DROP TRIGGER {fixture.Prefix}{name}_erasure_guard_insert; DROP TRIGGER {fixture.Prefix}{name}_erasure_guard_update;");
        await fixture.Sql($"DROP TABLE {fixture.Prefix}erasure_receipts; DROP TABLE {fixture.Prefix}erased_sources; DROP TABLE {fixture.Prefix}erased_records; DELETE FROM {fixture.Prefix}schema_migrations WHERE id='source-erasure-v1';");
        await fixture.Sql($"CREATE TABLE erasure_conflict(id integer); CREATE INDEX ix_{fixture.Prefix}erased_identity ON erasure_conflict(id);");
        await using (var failed = fixture.Store()) await Assert.ThrowsAnyAsync<DbException>(() => failed.InitializeAsync());
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}schema_migrations WHERE id='source-erasure-v1'")));
        await fixture.Sql("DROP TABLE erasure_conflict");
        await using var restarted = fixture.Store(); await restarted.InitializeAsync();
        Assert.True(Assert.Single((await restarted.ExportAsync(fixture.Partition)).Episodes).LegalHold);
        Assert.Equal(history, (await ((IMemoryRevisionReader)restarted).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items);
        Assert.Equal("memory_legal_hold_prevents_deletion", (await ((IMemoryErasureStore)restarted).PreviewEpisodeErasureAsync(fixture.Partition, source.Id)).BlockedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureDistinguishesNullAndEmptyApplicationsAndIncludesMovedRecordHistory(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode() with { Partition = fixture.Partition with { ApplicationId = null } };
        var independent = source with { Id = Guid.NewGuid(), Partition = source.Partition with { ApplicationId = "" } };
        await store.AppendEpisodeAsync(source); await store.AppendEpisodeAsync(independent);
        var moved = source.Partition with { ApplicationId = "moved-application" };
        var json = JsonSerializer.Serialize(moved, JsonOptions);
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET partition_key='{moved.StorageKey}',payload=jsonb_set(payload,'{{partition}}','{json}'::jsonb) WHERE id='{source.Id:D}'"
            : $"UPDATE {fixture.Prefix}episodes SET partition_key='{moved.StorageKey}',application_id='moved-application',payload=json_set(payload,'$.partition',json('{json}')) WHERE id='{source.Id:D}'");
        var erase = (IMemoryErasureStore)store; var preview = await erase.PreviewEpisodeErasureAsync(source.Partition, source.Id);
        Assert.Null(preview.BlockedReason); Assert.Equal(2, preview.Targets.Count);
        await erase.EraseEpisodeAsync(source.Partition, source.Id, preview.EvidenceToken);
        Assert.Empty((await store.ExportAsync(moved)).Episodes); Assert.Single((await store.ExportAsync(independent.Partition)).Episodes);
        await Assert.ThrowsAnyAsync<DbException>(() => store.AppendEpisodeAsync(source with { Id = Guid.NewGuid() }));
        await Assert.ThrowsAnyAsync<DbException>(() => store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(), Partition = moved }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ErasureFencesUnicodeSourceTypesWithEachStoresOwnCaseRules(string provider)
    {
        await using var fixture = await Fixture.Create(provider); await using var store = fixture.Store();
        var source = fixture.Episode() with { Source = new("İMPORT", "opaque-source") }; await store.AppendEpisodeAsync(source);
        var erase = (IMemoryErasureStore)store; var preview = await erase.PreviewEpisodeErasureAsync(source.Partition, source.Id);
        await erase.EraseEpisodeAsync(source.Partition, source.Id, preview.EvidenceToken);
        await Assert.ThrowsAnyAsync<DbException>(() => store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(),
            Partition = source.Partition with { AgentId = "new-audience" } }));
    }
}
