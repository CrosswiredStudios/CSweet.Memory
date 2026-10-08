using System.Data.Common;
using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    [Fact]
    public void FingerprintBindsImmutableEvidenceButAllowsPolicyChangesAndStableSerialization()
    {
        var now = DateTimeOffset.UtcNow;
        var source = MemorySourceIntegrity.Seal(new(Guid.NewGuid(), new("tenant", "app"), MemoryScope.Application,
            "evidence", "text/plain", new("user", "source", "author"), "checksum", now, now,
            Metadata: new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" }));
        Assert.True(MemorySourceIntegrity.IsVerified(source));
        Assert.True(MemorySourceIntegrity.IsVerified(source with { Sensitivity = MemorySensitivity.Restricted, LegalHold = true,
            ExpiresAt = now.AddDays(1), RecordedAt = now.AddDays(2), OccurredAt = now.ToOffset(TimeSpan.FromHours(-7)),
            Metadata = new Dictionary<string, string> { ["a"] = "first", ["z"] = "last" } }));
        foreach (var changed in new[] { source with { Content = "changed" }, source with { ContentType = "text/html" },
            source with { Source = source.Source with { Author = "other" } }, source with { Checksum = "new" },
            source with { OccurredAt = now.AddSeconds(1) }, source with { Scope = MemoryScope.User },
            source with { Partition = new("other", "app") }, source with { Metadata = null },
            source with { OperationalReferences = [new("work", "123", "1")] }, source with { SourceFingerprint = null } })
            Assert.False(MemorySourceIntegrity.IsVerified(changed));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ChangedEvidenceIsWithheldEverywhereAndCannotBeResealedInPlace(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var source = fixture.Episode() with { SourceFingerprint = "caller-forged" };
        await store.AppendEpisodeAsync(source);
        var stored = (await ((IMemorySourceReader)store).GetEpisodeAsync(fixture.Partition, source.Id))!;
        Assert.True(MemorySourceIntegrity.IsVerified(stored)); Assert.NotEqual(source.SourceFingerprint, stored.SourceFingerprint);
        var entity = fixture.Entity(source) with { CanonicalName = "Alpha" };
        var middle = entity with { Id = Guid.NewGuid(), CanonicalName = "Beta", ApplicationKey = null, SourceEpisodeIds = [] };
        var end = middle with { Id = Guid.NewGuid(), CanonicalName = "Gamma" };
        foreach (var item in new[] { entity, middle, end }) await store.UpsertEntityAsync(item);
        var direct = source with { Id = Guid.NewGuid(), Content = "separate" }; await store.AppendEpisodeAsync(direct);
        var now = DateTimeOffset.UtcNow;
        await store.WriteClaimAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "source", null, "evidence",
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, 1, 1, now, null, now));
        await store.WriteEdgeAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, entity.Id, "links", middle.Id,
            MemoryTrustTier.UnconfirmedUser, 1, now, null, false, now));
        var onward = new MemoryEdge(Guid.NewGuid(), fixture.Partition, direct.Id, middle.Id, "links", end.Id,
            MemoryTrustTier.UnconfirmedUser, 1, now, null, false, now); await store.WriteEdgeAsync(onward);
        await store.WriteProcedureAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, "source", "evidence", null, 1,
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, now, null, now));
        await store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "source", "evidence", 1, 100, true,
            MemoryTrustTier.Authoritative, now) { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [source.Id] });
        await store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, source.Id, MemoryLayer.Episodic, [1, 0], "model", now));
        Assert.Contains(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "Alpha")), x => x.Id == onward.Id);
        // Privileged test mutation leaves the original fingerprint: retrieval must detect this.
        await fixture.Sql(fixture.Postgres
            ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{content}}','\"changed\"') WHERE id='{source.Id}'"
            : $"UPDATE {fixture.Prefix}episodes SET payload=json_set(payload,'$.content','changed') WHERE id='{source.Id}'");
        Assert.Empty(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "source evidence Alpha", Embedding: [1, 0],
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Core, MemoryLayer.Semantic, MemoryLayer.Procedural })));
        Assert.DoesNotContain(await store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "source evidence", Embedding: [1, 0])), x => x.Id == source.Id);
        Assert.Null(await store.FindEntityAsync(fixture.Partition, "Alpha"));
        var projection = MemoryReadProjection.Create(await store.ExportAsync(fixture.Partition), fixture.Partition, MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(projection.Episodes, x => x.Id == source.Id);
        Assert.Empty(projection.Claims); Assert.Empty(projection.Blocks); Assert.Empty(projection.Procedures);
        Assert.DoesNotContain(projection.Edges, x => x.FromEntityId == entity.Id);
        foreach (var jsonValue in new[] { "\"replacement\"", "null" })
        {
            var error = await Assert.ThrowsAnyAsync<DbException>(() => fixture.Sql(fixture.Postgres
                ? $"UPDATE {fixture.Prefix}episodes SET payload=jsonb_set(payload,'{{sourceFingerprint}}','{jsonValue}') WHERE id='{source.Id}'"
                : $"UPDATE {fixture.Prefix}episodes SET payload=json_set(payload,'$.sourceFingerprint',json('{jsonValue}')) WHERE id='{source.Id}'"));
            Assert.Contains("memory_source_fingerprint_immutable", error.Message);
        }
        Assert.Equal(2, (await ((IMemoryRevisionReader)store).ReadRevisionsAsync(fixture.Partition, MemoryRecordKind.Episode, source.Id)).Items.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeNeverInventsFingerprintsForUnreviewedLegacyEvidence(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        var source = fixture.Episode();
        await using (var initial = fixture.Store()) await initial.AppendEpisodeAsync(source);
        await fixture.Sql(fixture.Postgres
            ? $"DROP TRIGGER {fixture.Prefix}source_fingerprint_guard ON {fixture.Prefix}episodes; DROP FUNCTION {fixture.Prefix}source_fingerprint_guard(); UPDATE {fixture.Prefix}episodes SET payload=payload-'sourceFingerprint';"
            : $"DROP TRIGGER {fixture.Prefix}source_fingerprint_guard; UPDATE {fixture.Prefix}episodes SET payload=json_remove(payload,'$.sourceFingerprint');");
        await fixture.Sql($"DELETE FROM {fixture.Prefix}schema_migrations WHERE id='source-fingerprints-v1'");
        await using var first = fixture.Store(); await using var second = fixture.Store();
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        Assert.Null(Assert.Single((await first.ExportAsync(fixture.Partition)).Episodes).SourceFingerprint);
        Assert.Empty(await first.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "source evidence")));
        Assert.Equal(1L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}schema_migrations WHERE id='source-fingerprints-v1'")));
        await using var restart = fixture.Store();
        Assert.Empty(await restart.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "source evidence")));
    }
}
