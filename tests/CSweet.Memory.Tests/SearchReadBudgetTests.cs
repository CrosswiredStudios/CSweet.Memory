using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed partial class RevisionHistoryTests
{
    private static string SqlText(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SearchCandidateAllowanceDistinguishesExhaustedDataFromAnUnexaminedValidResult(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        await store.InitializeAsync();
        var block = new MemoryBlock(Guid.Empty, fixture.Partition, "fixture", "Sentinel", 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow.AddSeconds(-1))
            { SourceEpisodeIds = [], Sensitivity = (MemorySensitivity)999 };
        // Malformed classification metadata matches the index but cannot establish an
        // eligible result. All rows count toward selection; no source fetch is needed.
        var values = Enumerable.Range(0, 8192).Select(i =>
        {
            var id = Guid.Parse(i.ToString("x8") + "-0000-0000-0000-000000000001");
            return $"('{id:D}',{SqlText(fixture.Partition.StorageKey)},'fixture-{i}',{(fixture.Postgres ? "true" : "1")}," +
                SqlText(JsonSerializer.Serialize(block with { Id = id, Name = "fixture-" + i }, JsonOptions)) + (fixture.Postgres ? "::jsonb)" : ")");
        });
        await fixture.Sql($"INSERT INTO {fixture.Prefix}blocks(id,partition_key,name,pinned,payload) VALUES {string.Join(',', values)}");
        var query = new MemorySearchRequest(fixture.Partition, MemoryScope.Agent, "Sentinel", Limit: 100,
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Core }) { IncludePinnedCore = true };
        Assert.Empty(await store.SearchAsync(query));
        var source = fixture.Episode();
        await store.AppendEpisodeAsync(source);
        var valid = block with { Id = Guid.Parse("7fffffff-0000-0000-0000-000000000001"), Partition = fixture.Partition,
            Name = "independent", SourceEpisodeIds = [source.Id], Sensitivity = MemorySensitivity.Personal };
        await store.WriteBlockAsync(valid);
        var error = await Assert.ThrowsAsync<MemorySearchBudgetExceededException>(() => store.SearchAsync(query));
        Assert.Equal("candidates", error.Budget);
        // The independent row is genuinely eligible; an explicit allowance failure must
        // not misreport the earlier saturated search as proving that it does not exist.
        Assert.Equal(valid.Id, Assert.Single(await store.SearchAsync(query with { IncludePinnedCore = false, Query = "independent" })).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SearchSourceAllowanceWithholdsPartialResultsWhenContributorFanoutExceedsTheSharedBound(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        await store.InitializeAsync();
        var groups = Enumerable.Range(0, 65).Select(_ => Enumerable.Range(0, 128)
            .Select(_ => MemorySourceIntegrity.Seal(fixture.Episode())).ToArray()).ToArray();
        var values = groups.SelectMany(group => group).Select(episode =>
            $"('{episode.Id:D}',{SqlText(fixture.Partition.StorageKey)},{SqlText(episode.Content)}," +
            SqlText(episode.OccurredAt.ToUniversalTime().ToString("O")) + "," +
            SqlText(JsonSerializer.Serialize(episode, JsonOptions)) + (fixture.Postgres ? "::jsonb)" :
                $",{SqlText(fixture.Partition.TenantId)},{SqlText(fixture.Partition.ApplicationId!)},{SqlText(fixture.Partition.AgentId!)},NULL,NULL,NULL,{(int)episode.Scope},0)"));
        var columns = fixture.Postgres ? "" : ",tenant_id,application_id,agent_id,user_id,conversation_id,custom_namespace,scope,legal_hold";
        await fixture.Sql($"INSERT INTO {fixture.Prefix}episodes(id,partition_key,content,occurred_at,payload{columns}) VALUES {string.Join(',', values)}");
        for (var i = 0; i < groups.Length; i++)
            await store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Sentinel " + i, "answer", 1, 100, true,
                MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow.AddSeconds(-1))
                { SourceEpisodeIds = groups[i].Select(episode => episode.Id).ToArray(), Sensitivity = MemorySensitivity.Personal });
        var query = new MemorySearchRequest(fixture.Partition, MemoryScope.Agent, "Sentinel",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Core });
        Assert.Single(await store.SearchAsync(query with { Limit = 1 }));
        var error = await Assert.ThrowsAsync<MemorySearchBudgetExceededException>(() => store.SearchAsync(query with { Limit = 100 }));
        Assert.Equal("sources", error.Budget);
    }
}
