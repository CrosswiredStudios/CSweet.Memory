namespace CSweet.Memory.Tests;

public sealed partial class ProvenanceStoreTests
{
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DerivativesRetainAllEvidenceAndInheritItsClassification(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var primary = await fixture.EpisodeAsync();
        var contributor = await fixture.EpisodeAsync(MemorySensitivity.Confidential);
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var claim = fixture.Claim(primary, from) with { SourceEpisodeIds = [contributor.Id] };
        var edge = fixture.Edge(primary, from, to) with { SourceEpisodeIds = [contributor.Id] };
        var procedure = fixture.Procedure(primary) with { SourceEpisodeIds = [contributor.Id] };
        await fixture.Store.WriteClaimAsync(claim);
        await fixture.Store.WriteEdgeAsync(edge);
        await fixture.Store.WriteProcedureAsync(procedure);
        var ids = new[] { claim.Id, edge.Id, procedure.Id };
        var results = await fixture.SearchAsync();
        foreach (var id in ids)
        {
            var result = Assert.Single(results, x => x.Id == id);
            Assert.Equal(MemorySensitivity.Confidential, result.Sensitivity);
            Assert.Equal(new[] { primary.Id, contributor.Id }.Order(), result.EpisodeIds.Order());
        }
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        var hidden = MemoryReadProjection.Create(raw, fixture.Partition, MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        Assert.Empty(hidden.Claims); Assert.Empty(hidden.Edges); Assert.Empty(hidden.Procedures);
        var visible = MemoryReadProjection.Create(raw, fixture.Partition, MemorySensitivity.Confidential, DateTimeOffset.UtcNow);
        var transfers = MemoryReadProjection.TransferItems(visible, fixture.Partition).Where(x => ids.Contains(x.MemoryId)).ToArray();
        Assert.Equal(3, transfers.Length);
        Assert.All(transfers, item =>
        {
            Assert.Equal(MemorySensitivity.Confidential, item.Sensitivity);
            Assert.Equal(new[] { primary.Id, contributor.Id }.Order(), item.EpisodeIds.Order());
        });
        // Existing direct-reference overloads must not silently ignore newly supplied evidence.
        Assert.Null(MemoryProvenance.ResolveClaim(claim, primary, from, null, DateTimeOffset.UtcNow));
        Assert.Null(MemoryProvenance.ResolveEdge(edge, primary, from, to, DateTimeOffset.UtcNow));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidContributorsExcludeAllDerivativeKindsWithOtherwiseValidReferences(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var primary = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var invalidIds = new[] { Guid.NewGuid(), (await fixture.EpisodeAsync(partition: fixture.Foreign)).Id,
            (await fixture.EpisodeAsync(expired: true)).Id, (await fixture.EpisodeAsync(future: true)).Id };
        foreach (var id in invalidIds)
        {
            await fixture.Store.WriteClaimAsync(fixture.Claim(primary, from) with { SourceEpisodeIds = [id] });
            await fixture.Store.WriteEdgeAsync(fixture.Edge(primary, from, to) with { SourceEpisodeIds = [id] });
            await fixture.Store.WriteProcedureAsync(fixture.Procedure(primary) with { SourceEpisodeIds = [id] });
        }
        Assert.All(await fixture.SearchAsync(), x => Assert.Equal(MemoryLayer.Episodic, x.Layer));
        var projection = MemoryReadProjection.Create(await fixture.Store.ExportAsync(fixture.Partition), fixture.Partition,
            MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
        Assert.Empty(projection.Claims); Assert.Empty(projection.Edges); Assert.Empty(projection.Procedures);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DerivativeReplayCannotDropEvidenceAndMalformedListsAreRejected(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var primary = await fixture.EpisodeAsync();
        var contributor = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var claim = fixture.Claim(primary, from) with { SourceEpisodeIds = [contributor.Id] };
        var edge = fixture.Edge(primary, from, to) with { SourceEpisodeIds = [contributor.Id] };
        var procedure = fixture.Procedure(primary) with { SourceEpisodeIds = [contributor.Id] };
        await fixture.Store.WriteClaimAsync(claim); await fixture.Store.WriteEdgeAsync(edge); await fixture.Store.WriteProcedureAsync(procedure);
        Assert.False((await fixture.Store.WriteClaimAsync(claim)).Created);
        Assert.False((await fixture.Store.WriteEdgeAsync(edge)).Created);
        Assert.False((await fixture.Store.WriteProcedureAsync(procedure)).Created);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteClaimAsync(claim with { SourceEpisodeIds = [] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteEdgeAsync(edge with { SourceEpisodeIds = [] }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.WriteProcedureAsync(procedure with { SourceEpisodeIds = [] }));
        foreach (var invalid in new IReadOnlyList<Guid>[] { null!, [Guid.Empty], Enumerable.Range(0, 129).Select(_ => Guid.NewGuid()).ToArray() })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteClaimAsync(claim with { SourceEpisodeIds = invalid }));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteEdgeAsync(edge with { SourceEpisodeIds = invalid }));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteProcedureAsync(procedure with { SourceEpisodeIds = invalid }));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacySingleSourcePayloadsStillReplayAfterAdditiveLineageUpgrade(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var source = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var claim = fixture.Claim(source, from);
        var edge = fixture.Edge(source, from, to);
        var procedure = fixture.Procedure(source);
        await fixture.Store.WriteClaimAsync(claim); await fixture.Store.WriteEdgeAsync(edge); await fixture.Store.WriteProcedureAsync(procedure);
        await using System.Data.Common.DbConnection connection = fixture.SqlitePath is { } path
            ? new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")
            : new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
        await connection.OpenAsync();
        foreach (var (suffix, id) in new[] { ("claims", claim.Id), ("edges", edge.Id), ("procedures", procedure.Id) })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = provider == "sqlite"
                ? $"UPDATE memory_{suffix} SET payload=json_remove(payload,'$.sourceEpisodeIds') WHERE id=@id"
                : $"UPDATE csweet_memory_{suffix} SET payload=payload - 'sourceEpisodeIds' WHERE id=@id";
            var parameter = command.CreateParameter(); parameter.ParameterName = "id";
            parameter.Value = provider == "sqlite" ? id.ToString("D") : id;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync();
        }
        Assert.False((await fixture.Store.WriteClaimAsync(claim)).Created);
        Assert.False((await fixture.Store.WriteEdgeAsync(edge)).Created);
        Assert.False((await fixture.Store.WriteProcedureAsync(procedure)).Created);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ContributorRemovalInvalidatesPreviouslyVisibleDerivatives(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var primary = await fixture.EpisodeAsync();
        var contributor = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var claim = fixture.Claim(primary, from) with { SourceEpisodeIds = [contributor.Id] };
        var edge = fixture.Edge(primary, from, to) with { SourceEpisodeIds = [contributor.Id] };
        var procedure = fixture.Procedure(primary) with { SourceEpisodeIds = [contributor.Id] };
        await fixture.Store.WriteClaimAsync(claim); await fixture.Store.WriteEdgeAsync(edge); await fixture.Store.WriteProcedureAsync(procedure);
        var ids = new[] { claim.Id, edge.Id, procedure.Id };
        Assert.Equal(3, (await fixture.SearchAsync()).Count(x => ids.Contains(x.Id)));
        await using System.Data.Common.DbConnection connection = fixture.SqlitePath is { } path
            ? new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")
            : new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = provider == "sqlite" ? "DELETE FROM memory_episodes WHERE id=@id" : "DELETE FROM csweet_memory_episodes WHERE id=@id";
        var parameter = command.CreateParameter(); parameter.ParameterName = "id";
        parameter.Value = provider == "sqlite" ? contributor.Id.ToString("D") : contributor.Id;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
        Assert.DoesNotContain(await fixture.SearchAsync(), x => ids.Contains(x.Id));
        var projection = MemoryReadProjection.Create(await fixture.Store.ExportAsync(fixture.Partition), fixture.Partition,
            MemorySensitivity.Restricted, DateTimeOffset.UtcNow);
        Assert.Empty(projection.Claims); Assert.Empty(projection.Edges); Assert.Empty(projection.Procedures);
    }
}
