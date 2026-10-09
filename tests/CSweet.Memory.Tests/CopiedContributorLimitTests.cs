using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    public static IEnumerable<object[]> CopiedContributorCases => Providers.Cast<object[]>()
        .SelectMany(provider => new[] { "core", "claim", "subject", "object", "procedure", "graph-roots", "graph-edges", "graph-targets" }
            .SelectMany(channel => new[] { "package", "suppressed", "expired" }
                .SelectMany(defect => new[] { 1, 2 }.Select(depth => new object[] { provider[0], channel, defect, depth }))));

    [Theory]
    [MemberData(nameof(CopiedContributorCases))]
    public async Task CopiedContributorsCannotConsumeResultOrTraversalLimits(string provider, string channel, string defect, int depth)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow.AddHours(1).AddTicks(7);
        var tenant = Guid.NewGuid().ToString("D");
        var sourceNamespace = EmployeeMemoryNamespaces.Employee(tenant, "source", "copied-limits");
        var targetNamespace = EmployeeMemoryNamespaces.Employee(tenant, "target", "copied-limits");
        var bridge = EmployeeMemoryNamespaces.Employee(tenant, "bridge", "copied-limits");
        var partition = targetNamespace.Partition;
        var access = new MemoryAccessContext(new(tenant, "reviewer"), "handoff", "review");
        var engine = new MemoryEngine(store, Options.Create(new AgentMemoryOptions()),
            authorizer: new AllowAllMemoryScopeAuthorizer(), redactor: new PassthroughMemoryRedactor());
        Guid Key(int index) => Guid.Parse(index.ToString("x8") + Guid.NewGuid().ToString("D")[8..]);
        var primary = await engine.IngestAsync(new(partition, targetNamespace.Scope, "primary evidence", new("user", "direct"),
            OccurredAt: now.AddDays(-2), Access: access, Sensitivity: MemorySensitivity.Internal));
        var original = await engine.IngestAsync(new(sourceNamespace.Partition, sourceNamespace.Scope, "secondary evidence", new("user", "upstream"),
            OccurredAt: now.AddDays(-2), ExpiresAt: defect == "expired" ? now.ToOffset(TimeSpan.FromHours(-7)) : null,
            Access: access, Sensitivity: MemorySensitivity.Internal));
        async Task<KnowledgeTransferPackage> Apply(MemoryNamespace from, MemoryNamespace to)
        {
            var draft = await engine.PrepareKnowledgeTransferAsync(new(from.Partition.AgentId!, to.Partition.AgentId!, [from], to,
                access, "Reviewed evidence", Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
            await engine.ApproveKnowledgeTransferAsync(new(draft.Id, access, true));
            return await engine.ApplyKnowledgeTransferAsync(new(draft.Id, access));
        }
        var first = await Apply(sourceNamespace, depth == 1 ? targetNamespace : bridge);
        var last = depth == 1 ? first : await Apply(bridge, targetNamespace);
        var secondary = (await ((IMemorySourceReader)store).GetEpisodeAsync(partition, last.AppliedEpisodeId!.Value))!;
        Assert.True(MemoryProvenance.IsCurrent(secondary, partition, secondary.Id, now.AddTicks(-1)));
        if (defect == "package")
            await ((IKnowledgeTransferStore)store).WriteKnowledgeTransferAsync(first with { Status = KnowledgeTransferStatus.Rejected });
        if (defect == "suppressed")
            await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(sourceNamespace.Partition, original.Id);
            async Task<MemoryEntity> Entity(string name, int index, bool invalid = false)
            {
                var entity = new MemoryEntity(Key(index), partition, "topic", name, [], null, false, now.AddDays(-1), now.AddDays(-1))
                { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = invalid ? [secondary.Id] : [primary.Id] };
                return entity with { Id = (await store.UpsertEntityAsync(entity)).Id };
            }
            var root = await Entity("Sentinel owner", int.MaxValue);
            var target = await Entity("destination", int.MaxValue);
            var invalidTarget = await Entity("blocked destination", 0, invalid: channel == "graph-targets");
            var invalidCount = channel is "graph-edges" or "graph-targets" ? 512 : 40;
            var expected = Guid.Empty;
            for (var index = 0; index <= invalidCount; index++)
            {
                var valid = index == invalidCount;
                var id = Key(valid ? int.MaxValue : index);
                var sources = valid ? new[] { primary.Id } : new[] { secondary.Id };
                switch (channel)
                {
                    case "core":
                        await store.WriteBlockAsync(new(id, partition, $"Sentinel core {index}", "Sentinel answer", 1, 100, true,
                            MemoryTrustTier.Authoritative, now.AddDays(-1))
                        { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = sources });
                        break;
                    case "procedure":
                        await store.WriteProcedureAsync(new(id, partition, primary.Id, $"Sentinel procedure {index}", "Sentinel answer", null,
                            1, MemoryTrustTier.Authoritative, MemoryConfirmationState.Confirmed, now.AddDays(-1), null, now.AddDays(1))
                        { SourceEpisodeIds = sources });
                        break;
                    case "graph-roots":
                        // More invalid roots than the 32-root bound precede the only useful root.
                        if (!valid) { await Entity($"Sentinel root {index}", index, invalid: true); continue; }
                        goto case "graph-edges";
                    case "graph-edges":
                    case "graph-targets":
                        await store.WriteEdgeAsync(new(id, partition, primary.Id, root.Id, $"connects-{index}", valid ? target.Id : invalidTarget.Id,
                            MemoryTrustTier.Authoritative, 1, now.AddDays(-1), null, true, now.AddDays(1))
                        { SourceEpisodeIds = channel == "graph-edges" ? sources : [primary.Id] });
                        break;
                    default:
                        var subject = channel == "subject" && !valid ? await Entity($"Sentinel subject {index}", index, true) : root;
                        var objectEntity = channel == "object" && !valid ? invalidTarget : target;
                        if (channel == "object" && !valid && invalidTarget.SourceEpisodeIds[0] != secondary.Id)
                        {
                            invalidTarget = await Entity("blocked object", index, true);
                            objectEntity = invalidTarget;
                        }
                        await store.WriteClaimAsync(new(id, partition, primary.Id, subject.Id, "Sentinel decision", objectEntity.Id, "Sentinel answer",
                            MemoryTrustTier.Authoritative, MemoryConfirmationState.Confirmed, MemorySensitivity.Internal,
                            1, 1, now.AddDays(-1), null, now.AddDays(1))
                        { SourceEpisodeIds = channel == "claim" ? sources : [primary.Id] });
                        break;
                }
                if (valid) expected = id;
            }
            var layer = channel == "core" ? MemoryLayer.Core : channel == "procedure" ? MemoryLayer.Procedural : MemoryLayer.Semantic;
            var query = new MemorySearchRequest(partition, targetNamespace.Scope, "Sentinel", Limit: 1,
                AsOf: now.ToOffset(TimeSpan.FromHours(5.5)), Layers: new HashSet<MemoryLayer> { layer });
            var found = Assert.Single(await store.SearchAsync(query));
            Assert.Equal(expected, found.Id);
            Assert.Equal(MemorySensitivity.Internal, found.Sensitivity);
            Assert.DoesNotContain(secondary.Id, found.EpisodeIds);
            if (channel == "core")
                Assert.Equal(expected, Assert.Single(await store.SearchAsync(query with { Query = "unrelated", IncludePinnedCore = true })).Id);
            if (channel.StartsWith("graph-", StringComparison.Ordinal)) Assert.Equal("graph", found.RetrievalChannel);
    }
}
