namespace CSweet.Memory.Tests;

public sealed class ContributorLimitTests
{
    public static IEnumerable<object[]> ObjectCases => ProvenanceStoreTests.Providers.Cast<object[]>()
        .SelectMany(provider => new[] { false, true }.Select(foreign => new object[] { provider[0], foreign }));

    [Theory]
    [MemberData(nameof(ObjectCases))]
    public async Task AReferencedMissingOrForeignObjectCannotConsumeTheClaimLimit(string provider, bool foreignObject)
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-object-limit-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString("D"), "object-limits");
        var foreign = partition with { TenantId = Guid.NewGuid().ToString("D") };
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        try
        {
            await store.InitializeAsync();
            var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "object evidence", "text/plain",
                new("user", "fixture"), "object-checksum", now.AddDays(-1), now.AddDays(1));
            await store.AppendEpisodeAsync(source);
            var subject = new MemoryEntity(Guid.NewGuid(), partition, "topic", "Sentinel", [], null, false, now, now)
                { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [source.Id] };
            subject = subject with { Id = (await store.UpsertEntityAsync(subject)).Id };
            var objectId = Guid.NewGuid();
            if (foreignObject)
                objectId = (await store.UpsertEntityAsync(subject with { Id = objectId, Partition = foreign, SourceEpisodeIds = [] })).Id;
            var invalid = new MemoryClaim(Guid.Parse("00000000" + Guid.NewGuid().ToString("D")[8..]), partition,
                source.Id, subject.Id, "Sentinel decision", objectId, "Sentinel answer", MemoryTrustTier.Authoritative,
                MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, 1, 1, now.AddDays(-1), null, now.AddDays(1));
            var valid = invalid with { Id = Guid.Parse("7fffffff" + Guid.NewGuid().ToString("D")[8..]), ObjectEntityId = null };
            await store.WriteClaimAsync(invalid);
            await store.WriteClaimAsync(valid);
            var found = await store.SearchAsync(new(partition, MemoryScope.Application, "Sentinel", Limit: 1, AsOf: now,
                Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic }));
            Assert.Equal(valid.Id, Assert.Single(found).Id);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            if (foreignObject) await store.DeleteScopeAsync(foreign);
            await store.DisposeAsync();
            if (provider == "sqlite") foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    public static IEnumerable<object[]> Cases => ProvenanceStoreTests.Providers.Cast<object[]>()
        .SelectMany(provider => new[] { "core", "claim", "subject", "object", "procedure", "graph-roots", "graph-edges", "graph-targets" }
            .SelectMany(channel => new[] { "future", "expired", "suppressed", "missing", "foreign" }
                .Select(defect => new object[] { provider[0], channel, defect })));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidSecondaryContributorsCannotHideAnEligibleResultAtTheCandidateLimit(
        string provider, string channel, string defect)
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-contributor-limit-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString("D"), "contributor-limits");
        var foreign = new MemoryPartition(Guid.NewGuid().ToString("D"), "contributor-limits");
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
        Guid Key(int index) => Guid.Parse(index.ToString("x8") + Guid.NewGuid().ToString("D")[8..]);
        var primary = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "primary evidence", "text/plain",
            new("user", "fixture-primary"), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("primary evidence"))),
            now.AddDays(-2), now.AddDays(2), ExpiresAt: now.AddDays(2));
        var secondary = primary with
        {
            Id = Guid.NewGuid(), Content = "secondary evidence",
            Source = new("user", "fixture-secondary"),
            Checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("secondary evidence"))),
            Partition = defect == "foreign" ? foreign : partition,
            OccurredAt = defect == "future" ? now.AddTicks(1).ToOffset(TimeSpan.FromHours(14)) : primary.OccurredAt,
            ExpiresAt = defect == "expired" ? now.ToOffset(TimeSpan.FromHours(-7)) : primary.ExpiresAt
        };
        try
        {
            await store.InitializeAsync();
            await store.AppendEpisodeAsync(primary);
            if (defect != "missing") await store.AppendEpisodeAsync(secondary);
            if (defect == "suppressed")
            {
                await ((IMemorySuppressionStore)store).SuppressEpisodeAsync(partition, secondary.Id);
                Assert.False((await ((IMemorySourceReader)store).GetEpisodeAsync(partition, primary.Id))!.IsSuppressed);
            }
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
            var query = new MemorySearchRequest(partition, MemoryScope.Application, "Sentinel", Limit: 1,
                AsOf: now.ToOffset(TimeSpan.FromHours(5.5)), Layers: new HashSet<MemoryLayer> { layer });
            var found = Assert.Single(await store.SearchAsync(query));
            Assert.Equal(expected, found.Id);
            Assert.Equal(MemorySensitivity.Internal, found.Sensitivity);
            Assert.DoesNotContain(secondary.Id, found.EpisodeIds);
            if (channel == "core")
                Assert.Equal(expected, Assert.Single(await store.SearchAsync(query with { Query = "unrelated", IncludePinnedCore = true })).Id);
            if (channel.StartsWith("graph-", StringComparison.Ordinal)) Assert.Equal("graph", found.RetrievalChannel);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            if (defect == "foreign") await store.DeleteScopeAsync(foreign);
            await store.DisposeAsync();
            if (provider == "sqlite") foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
