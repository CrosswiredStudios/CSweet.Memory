namespace CSweet.Memory.Tests;

public sealed partial class ProvenanceStoreTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EntityAndBlockSourcesConstrainReadsAndTransferEvidence(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var confidential = await fixture.EpisodeAsync(MemorySensitivity.Confidential);
        var direct = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory subject");
        var to = await fixture.EntityAsync("memory target");
        await fixture.Store.UpsertEntityAsync(@from with { SourceEpisodeIds = [confidential.Id] });
        var claim = fixture.Claim(direct, from);
        var edge = fixture.Edge(direct, from, to);
        await fixture.Store.WriteClaimAsync(claim);
        await fixture.Store.WriteEdgeAsync(edge);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "memory", "memory summary", 1, 500,
            true, MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow)
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [confidential.Id] };
        await fixture.Store.WriteBlockAsync(block);

        foreach (var item in (await fixture.SearchAsync()).Where(x => new[] { claim.Id, edge.Id, block.Id }.Contains(x.Id)))
        {
            Assert.Equal(MemorySensitivity.Confidential, item.Sensitivity);
            Assert.Contains(confidential.Id, item.EpisodeIds);
        }
        Assert.Equal(3, (await fixture.SearchAsync()).Count(x => new[] { claim.Id, edge.Id, block.Id }.Contains(x.Id)));
        Assert.Equal(MemorySensitivity.Confidential, (await fixture.Store.FindEntityAsync(fixture.Partition, from.CanonicalName))!.Sensitivity);
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        var allowed = MemoryReadProjection.Create(raw, fixture.Partition, MemorySensitivity.Confidential, DateTimeOffset.UtcNow);
        var transfer = MemoryReadProjection.TransferItems(allowed, fixture.Partition).ToArray();
        foreach (var id in new[] { claim.Id, edge.Id, block.Id })
            Assert.Contains(confidential.Id, Assert.Single(transfer, x => x.MemoryId == id).EpisodeIds);
        var withheld = MemoryReadProjection.Create(raw, fixture.Partition, MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        Assert.Empty(withheld.Claims);
        Assert.Empty(withheld.Edges);
        Assert.Empty(withheld.Blocks);
        Assert.DoesNotContain(withheld.Entities, x => x.Id == from.Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidContributingSourceCannotBeHiddenByValidDirectEvidence(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var direct = await fixture.EpisodeAsync();
        var expired = await fixture.EpisodeAsync(expired: true);
        var foreign = await fixture.EpisodeAsync(partition: fixture.Foreign);
        foreach (var sourceId in new[] { expired.Id, foreign.Id, Guid.NewGuid() })
        {
            var entity = await fixture.EntityAsync($"memory {sourceId:N}");
            await fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = [sourceId],
                ApplicationKey = $"key:{sourceId}", Aliases = [$"alias:{sourceId}"] });
            await fixture.Store.WriteClaimAsync(fixture.Claim(direct, entity));
            await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, sourceId.ToString(), "memory summary",
                1, 100, true, MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow)
                { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [sourceId] });
            Assert.Null(await fixture.Store.FindEntityAsync(fixture.Partition, entity.CanonicalName));
            Assert.Null(await fixture.Store.FindEntityAsync(fixture.Partition, $"alias:{sourceId}"));
            Assert.Null(await fixture.Store.FindEntityByApplicationKeyAsync(fixture.Partition, $"key:{sourceId}"));
            Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEntityAsync(fixture.Partition, entity.Id));
        }
        Assert.All(await fixture.SearchAsync(), x => Assert.Equal(direct.Id, x.Id));
        var projection = MemoryReadProjection.Create(await fixture.Store.ExportAsync(fixture.Partition), fixture.Partition,
            MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
        Assert.Empty(projection.Entities);
        Assert.Empty(projection.Claims);
        Assert.Empty(projection.Blocks);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentUpdatesAndLegacyPayloadsCannotEraseContributingSources(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var one = await fixture.EpisodeAsync();
        var two = await fixture.EpisodeAsync(MemorySensitivity.Confidential);
        var entity = await fixture.EntityAsync("memory");
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "memory", "memory", 1, 100,
            true, MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Internal };
        await fixture.Store.WriteBlockAsync(block);
        await Task.WhenAll(
            Task.Run(() => fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = [one.Id] })),
            Task.Run(() => fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = [two.Id] })));
        await Task.WhenAll(
            Task.Run(() => fixture.Store.WriteBlockAsync(block with { SourceEpisodeIds = [one.Id], Sensitivity = MemorySensitivity.Confidential })),
            Task.Run(() => fixture.Store.WriteBlockAsync(block with { SourceEpisodeIds = [two.Id] })));
        // An older producer's empty lineage cannot clear the accumulated dependencies or sensitivity.
        await fixture.Store.UpsertEntityAsync(entity with { ApplicationKey = "known" });
        await fixture.Store.UpsertEntityAsync(entity with { ApplicationKey = "known", CanonicalName = "renamed", Id = Guid.NewGuid() });
        await fixture.Store.WriteBlockAsync(block);
        var snapshot = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal(new[] { one.Id, two.Id }.Order(), Assert.Single(snapshot.Entities).SourceEpisodeIds.Order());
        Assert.Equal(new[] { one.Id, two.Id }.Order(), Assert.Single(snapshot.Blocks).SourceEpisodeIds.Order());
        Assert.Equal(MemorySensitivity.Confidential, Assert.Single(snapshot.Blocks).Sensitivity);
        Assert.Equal(entity.Id, Assert.Single(snapshot.Entities).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LineageLimitRejectsUpdateWithoutLosingExistingDependencies(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var ids = Enumerable.Range(0, MemoryProvenance.MaximumSourceEpisodes).Select(_ => Guid.NewGuid()).ToArray();
        var entity = await fixture.EntityAsync("memory");
        await fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = ids, ApplicationKey = "known" });
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "memory", "original", 1, 100,
            true, MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow) { SourceEpisodeIds = ids };
        await fixture.Store.WriteBlockAsync(block);
        var extra = new[] { Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = extra }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = extra, ApplicationKey = "known" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteBlockAsync(block with { SourceEpisodeIds = extra, Content = "changed" }));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal(ids.Order(), Assert.Single(raw.Entities).SourceEpisodeIds.Order());
        Assert.Equal(ids.Order(), Assert.Single(raw.Blocks).SourceEpisodeIds.Order());
        Assert.Equal("original", Assert.Single(raw.Blocks).Content);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LaterRecallRechecksExpiryOfEntityAndBlockContributions(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var now = DateTimeOffset.UtcNow;
        var direct = await fixture.EpisodeAsync();
        var expiring = direct with { Id = Guid.NewGuid(), ExpiresAt = now.AddHours(1) };
        await fixture.Store.AppendEpisodeAsync(expiring);
        var entity = await fixture.EntityAsync("memory");
        await fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = [expiring.Id] });
        var claim = fixture.Claim(direct, entity);
        await fixture.Store.WriteClaimAsync(claim);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "memory", "memory", 1, 100,
            true, MemoryTrustTier.Authoritative, now) { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [expiring.Id] };
        await fixture.Store.WriteBlockAsync(block);
        Assert.Contains(await fixture.SearchAsync(), x => x.Id == claim.Id);
        Assert.Contains(await fixture.SearchAsync(), x => x.Id == block.Id);
        var later = await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.Agent, "memory", AsOf: now.AddHours(2)));
        Assert.DoesNotContain(later, x => x.Id == claim.Id || x.Id == block.Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SourceReclassificationAndRemovalTakeEffectOnSubsequentReads(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var source = await fixture.EpisodeAsync();
        var entity = await fixture.EntityAsync("memory");
        await fixture.Store.UpsertEntityAsync(entity with { SourceEpisodeIds = [source.Id] });
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "memory", "memory summary", 1, 100,
            true, MemoryTrustTier.Authoritative, DateTimeOffset.UtcNow)
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [source.Id] };
        await fixture.Store.WriteBlockAsync(block);
        Assert.Equal(MemorySensitivity.Internal, (await fixture.Store.FindEntityAsync(fixture.Partition, "memory"))!.Sensitivity);
        // Simulate a privileged classification/lifecycle change in the disposable test database.
        await using System.Data.Common.DbConnection connection = fixture.SqlitePath is { } path
            ? new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")
            : new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var table = provider == "sqlite" ? "memory_episodes" : "csweet_memory_episodes";
        var parameter = command.CreateParameter(); parameter.ParameterName = "id";
        parameter.Value = provider == "sqlite" ? source.Id.ToString("D") : source.Id;
        command.Parameters.Add(parameter);
        command.CommandText = provider == "sqlite"
            ? $"UPDATE {table} SET payload=json_set(payload,'$.sensitivity',4) WHERE id=@id"
            : $"UPDATE {table} SET payload=jsonb_set(payload,'{{sensitivity}}','4'::jsonb) WHERE id=@id";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(MemorySensitivity.Restricted, (await fixture.Store.FindEntityAsync(fixture.Partition, "memory"))!.Sensitivity);
        Assert.Equal(MemorySensitivity.Restricted, Assert.Single(await fixture.SearchAsync(), x => x.Id == block.Id).Sensitivity);
        command.CommandText = $"DELETE FROM {table} WHERE id=@id";
        await command.ExecuteNonQueryAsync();
        Assert.Null(await fixture.Store.FindEntityAsync(fixture.Partition, "memory"));
        Assert.DoesNotContain(await fixture.SearchAsync(), x => x.Id == block.Id);
    }
}
