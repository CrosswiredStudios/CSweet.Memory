namespace CSweet.Memory.Tests;

public sealed partial class ProvenanceStoreTests
{
    // PostgreSQL tests use a dedicated test database, never application data.
    public static TheoryData<string> Providers => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"))
        ? new() { "sqlite" } : new() { "sqlite", "postgres" };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DerivedResultsInheritSourceAndEntitySensitivity(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var source = await fixture.EpisodeAsync(MemorySensitivity.Confidential);
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("target");
        var claim = fixture.Claim(source, from);
        await fixture.Store.WriteClaimAsync(claim);
        var edge = fixture.Edge(source, from, to);
        await fixture.Store.WriteEdgeAsync(edge);
        var procedure = fixture.Procedure(source);
        await fixture.Store.WriteProcedureAsync(procedure);
        var results = await fixture.SearchAsync();
        foreach (var id in new[] { claim.Id, edge.Id, procedure.Id })
            Assert.Equal(MemorySensitivity.Confidential, Assert.Single(results, x => x.Id == id).Sensitivity);

        await fixture.Store.UpsertEntityAsync(@from with { Sensitivity = MemorySensitivity.Restricted });
        results = await fixture.SearchAsync();
        Assert.Equal(MemorySensitivity.Restricted, Assert.Single(results, x => x.Id == claim.Id).Sensitivity);
        var safe = MemoryReadProjection.Create(await fixture.Store.ExportAsync(fixture.Partition), fixture.Partition,
            MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        Assert.Empty(safe.Claims);
        Assert.Empty(safe.Edges);
        Assert.Empty(safe.Procedures);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ForeignAndMissingReferencesNeverBecomeCandidates(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var source = await fixture.EpisodeAsync();
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("memory target");
        var foreignSource = await fixture.EpisodeAsync(partition: fixture.Foreign);
        var foreignEntity = await fixture.EntityAsync("foreign memory secret", fixture.Foreign);
        foreach (var invalid in new[]
        {
            fixture.Claim(source, from) with { EpisodeId = foreignSource.Id },
            fixture.Claim(source, from) with { EpisodeId = Guid.NewGuid() },
            fixture.Claim(source, from) with { SubjectEntityId = foreignEntity.Id },
            fixture.Claim(source, from) with { ObjectEntityId = foreignEntity.Id }
        }) await fixture.Store.WriteClaimAsync(invalid);
        await fixture.Store.WriteEdgeAsync(fixture.Edge(source, from, foreignEntity));
        await fixture.Store.WriteEdgeAsync(fixture.Edge(source, foreignEntity, to));
        await fixture.Store.WriteEdgeAsync(fixture.Edge(foreignSource, from, to));
        await fixture.Store.WriteProcedureAsync(fixture.Procedure(foreignSource));
        await fixture.Store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, foreignSource.Id,
            MemoryLayer.Episodic, [1, 0], "test", DateTimeOffset.UtcNow));

        var results = await fixture.SearchAsync();
        Assert.All(results, item => Assert.Equal(source.Id, item.Id));
        Assert.DoesNotContain(results, item => item.RetrievalChannel == "vector");
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, foreignSource.Id));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEntityAsync(fixture.Partition, foreignEntity.Id));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FutureAndExpiredEvidenceCannotSupportAnyRetrievalChannel(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var from = await fixture.EntityAsync("memory source");
        var to = await fixture.EntityAsync("target");
        foreach (var future in new[] { false, true })
        {
            var source = await fixture.EpisodeAsync(future: future, expired: !future);
            await fixture.Store.WriteClaimAsync(fixture.Claim(source, from));
            await fixture.Store.WriteEdgeAsync(fixture.Edge(source, from, to));
            await fixture.Store.WriteProcedureAsync(fixture.Procedure(source));
            await fixture.Store.WriteEmbeddingAsync(new(Guid.NewGuid(), fixture.Partition, source.Id,
                MemoryLayer.Episodic, [1, 0], "test", DateTimeOffset.UtcNow));
        }
        Assert.Empty(await fixture.SearchAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EntityUpsertsPreserveIdentityAndNeverLowerClassification(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var original = await fixture.EntityAsync("memory source");
        await fixture.Store.UpsertEntityAsync(original with { Sensitivity = MemorySensitivity.Restricted });
        var result = await fixture.Store.UpsertEntityAsync(original with { Id = Guid.NewGuid() });
        var stored = await ((IMemorySourceReader)fixture.Store).GetEntityAsync(fixture.Partition, original.Id);
        Assert.Equal(original.Id, result.Id);
        Assert.Equal(MemorySensitivity.Restricted, stored!.Sensitivity);
        await fixture.Store.UpsertEntityAsync(stored with { ApplicationKey = "memory:source" });
        await fixture.Store.UpsertEntityAsync(original with { Id = Guid.NewGuid(), ApplicationKey = "memory:source", CanonicalName = "renamed" });
        Assert.Equal(MemorySensitivity.Restricted, (await fixture.Store.FindEntityByApplicationKeyAsync(fixture.Partition, "memory:source"))!.Sensitivity);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExactSourceReadsRejectLegacyKeyAliases(string provider)
    {
        await using var fixture = await Fixture.CreateAsync(provider);
        var source = await fixture.EpisodeAsync();
        var entity = await fixture.EntityAsync("memory source");
        var alias = fixture.Partition with { AgentId = null, UserId = fixture.Partition.AgentId };
        Assert.Equal(fixture.Partition.Key, alias.Key);
        var reader = (IMemorySourceReader)fixture.Store;
        Assert.Null(await reader.GetEpisodeAsync(alias, source.Id));
        Assert.Null(await reader.GetEntityAsync(alias, entity.Id));
        Assert.Empty(await fixture.Store.SearchAsync(new(alias, MemoryScope.User, "memory")));
    }

    private sealed class Fixture(IMemoryStore store, string? sqlitePath) : IAsyncDisposable
    {
        public IMemoryStore Store { get; } = store;
        public string? SqlitePath => sqlitePath;
        public MemoryPartition Partition { get; } = new(Guid.NewGuid().ToString(), "test", "owner");
        public MemoryPartition Foreign { get; } = new(Guid.NewGuid().ToString(), "test", "other");
        public static async Task<Fixture> CreateAsync(string provider)
        {
            var path = provider == "sqlite" ? Path.Combine(Path.GetTempPath(), $"provenance-{Guid.NewGuid():N}.db") : null;
            IMemoryStore store = path is not null ? new SqliteMemoryStore(path)
                : new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!);
            await store.InitializeAsync();
            return new(store, path);
        }
        public async Task<MemoryEpisode> EpisodeAsync(MemorySensitivity sensitivity = MemorySensitivity.Internal,
            MemoryPartition? partition = null, bool future = false, bool expired = false)
        {
            var now = DateTimeOffset.UtcNow;
            var episode = new MemoryEpisode(Guid.NewGuid(), partition ?? Partition, MemoryScope.Agent, "memory evidence",
                "text/plain", new("user", "test"), "checksum", future ? now.AddDays(1) : now.AddDays(-1), now,
                ExpiresAt: expired ? now.AddMinutes(-1) : null, Sensitivity: sensitivity);
            await Store.AppendEpisodeAsync(episode);
            return episode;
        }
        public async Task<MemoryEntity> EntityAsync(string name, MemoryPartition? partition = null)
        {
            var entity = new MemoryEntity(Guid.NewGuid(), partition ?? Partition, "learned:topic", name, [], null,
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Internal };
            await Store.UpsertEntityAsync(entity);
            return entity;
        }
        public MemoryClaim Claim(MemoryEpisode source, MemoryEntity subject) => new(Guid.NewGuid(), Partition,
            source.Id, subject.Id, "memory", null, "derived memory", MemoryTrustTier.AgentInference,
            MemoryConfirmationState.NotRequired, MemorySensitivity.Public, 1, 1, DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow);
        public MemoryEdge Edge(MemoryEpisode source, MemoryEntity from, MemoryEntity to) => new(Guid.NewGuid(), Partition,
            source.Id, from.Id, "memory", to.Id, MemoryTrustTier.AgentInference, 1, DateTimeOffset.UtcNow.AddDays(-1), null, true, DateTimeOffset.UtcNow);
        public ProceduralMemory Procedure(MemoryEpisode source) => new(Guid.NewGuid(), Partition, source.Id, "memory",
            "derived memory procedure", null, 1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Confirmed,
            DateTimeOffset.UtcNow.AddDays(-1), null, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<MemoryCandidate>> SearchAsync() => Store.SearchAsync(new(Partition, MemoryScope.Agent, "memory", Embedding: [1, 0]));
        public async ValueTask DisposeAsync()
        {
            await Store.DeleteScopeAsync(Partition);
            await Store.DeleteScopeAsync(Foreign);
            await Store.DisposeAsync();
            if (sqlitePath is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }
}
