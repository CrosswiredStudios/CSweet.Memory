using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    public static IEnumerable<object[]> CopiedPrimaryCases => Providers.Cast<object[]>()
        .SelectMany(provider => new[] { "episodic", "vector", "claim", "procedure" }
            .SelectMany(channel => new[] { "package", "suppressed", "expired" }
                .SelectMany(defect => new[] { 1, 2 }.Select(depth => new object[] { provider[0], channel, defect, depth }))));

    [Theory]
    [MemberData(nameof(CopiedPrimaryCases))]
    public async Task CopiedPrimaryEvidenceIsVerifiedBeforeRankingAndLimits(string provider, string channel, string defect, int depth)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow.AddHours(1).AddTicks(7);
        var tenant = Guid.NewGuid().ToString("D");
        var source = EmployeeMemoryNamespaces.Employee(tenant, "source", "primary-limits");
        var bridge = EmployeeMemoryNamespaces.Employee(tenant, "bridge", "primary-limits");
        var target = EmployeeMemoryNamespaces.Employee(tenant, "target", "primary-limits");
        var access = new MemoryAccessContext(new(tenant, "reviewer"), "handoff", "review");
        var engine = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()),
            authorizer: new AllowAllMemoryScopeAuthorizer(), redactor: new PassthroughMemoryRedactor());
        var upstream = await engine.IngestAsync(new(source.Partition, source.Scope, "Sentinel Sentinel Sentinel Sentinel Sentinel",
            new("user", "upstream"), OccurredAt: now.AddDays(-2),
            ExpiresAt: defect == "expired" ? now.ToOffset(TimeSpan.FromHours(-7)) : null,
            Access: access, Sensitivity: MemorySensitivity.Internal));
        async Task<KnowledgeTransferPackage> Apply(MemoryNamespace from, MemoryNamespace to)
        {
            var draft = await engine.PrepareKnowledgeTransferAsync(new(from.Partition.AgentId!, to.Partition.AgentId!, [from], to,
                access, "Reviewed evidence", Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, access, true));
            return await engine.ApplyKnowledgeTransferAsync(new(draft.Id, access));
        }
        var first = await Apply(source, depth == 1 ? target : bridge);
        var last = depth == 1 ? first : await Apply(bridge, target);
        var copied = last.AppliedEpisodeId!.Value;
        var independent = await engine.IngestAsync(new(target.Partition, target.Scope,
            "Sentinel " + string.Join(' ', Enumerable.Range(0, 400).Select(i => "independent" + i)),
            new("user", "independent"), OccurredAt: now.AddDays(-3), Access: access, Sensitivity: MemorySensitivity.Internal));
        Guid Key(int index) => Guid.Parse(index.ToString("x8") + Guid.NewGuid().ToString("D")[8..]);
        var expected = independent.Id;
        var firstInvalid = copied;
        var count = channel == "vector" ? 1024 : channel is "claim" or "procedure" ? 40 : 0;
        var entity = new MemoryEntity(Guid.NewGuid(), target.Partition, "topic", "owner", [], null, false,
            now.AddDays(-1), now.AddDays(-1)) { SourceEpisodeIds = [independent.Id], Sensitivity = MemorySensitivity.Internal };
        if (channel == "claim") entity = entity with { Id = (await store.UpsertEntityAsync(entity)).Id };
        for (var index = 0; index <= count && channel != "episodic"; index++)
        {
            var valid = index == count;
            var id = Key(valid ? int.MaxValue : index);
            var episodeId = valid ? independent.Id : copied;
            switch (channel)
            {
                case "vector":
                    await store.WriteEmbeddingAsync(new(id, target.Partition, episodeId, MemoryLayer.Episodic,
                        valid ? [0.8f, 0.6f] : [1f, 0f], "fixture", now.AddDays(-1)));
                    break;
                case "claim":
                    await store.WriteClaimAsync(new(id, target.Partition, episodeId, entity.Id, "Sentinel", null, "answer",
                        MemoryTrustTier.Authoritative, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1,
                        now.AddDays(-1), null, now.AddDays(1)));
                    break;
                case "procedure":
                    await store.WriteProcedureAsync(new(id, target.Partition, episodeId, "Sentinel", "answer", null,
                        1, MemoryTrustTier.Authoritative, MemoryConfirmationState.Confirmed, now.AddDays(-1), null, now.AddDays(1)));
                    break;
            }
            if (channel is "claim" or "procedure")
            {
                if (index == 0) firstInvalid = id;
                if (valid) expected = id;
            }
        }
        var layer = channel is "episodic" or "vector" ? MemoryLayer.Episodic :
            channel == "claim" ? MemoryLayer.Semantic : MemoryLayer.Procedural;
        var query = new MemorySearchRequest(target.Partition, target.Scope, channel == "vector" ? "unmatchedword" : "Sentinel",
            Limit: 1, Embedding: channel == "vector" ? new float[] { 1, 0 } : null,
            AsOf: now.AddTicks(-1), Layers: new HashSet<MemoryLayer> { layer });
        // Establish that the invalidated record actually precedes the independent result.
        Assert.Equal(firstInvalid, Assert.Single(await store.SearchAsync(query)).Id);
        if (defect == "package")
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(first with { Status = KnowledgeTransferStatus.Rejected });
        if (defect == "suppressed")
            await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(source.Partition, upstream.Id);
        var found = Assert.Single(await store.SearchAsync(query with { AsOf = now.ToOffset(TimeSpan.FromHours(5.5)) }));
        Assert.Equal(expected, found.Id);
        Assert.DoesNotContain(copied, found.EpisodeIds);
        if (channel == "vector") Assert.Equal("vector", found.RetrievalChannel);
    }
}
