using System.Text.Json;
using Neo4j.Driver;

namespace CSweet.Memory;

public sealed class Neo4jMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDriver _driver;
    private readonly string _database;

    public Neo4jMemoryStore(string uri, string user, string password, string database = "neo4j")
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        _database = database;
    }

    public MemoryStoreCapabilities Capabilities => MemoryStoreCapabilities.Transactions |
        MemoryStoreCapabilities.RecursiveTraversal | MemoryStoreCapabilities.TemporalQueries |
        MemoryStoreCapabilities.ChangeHistory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _driver.VerifyConnectivityAsync();
        foreach (var query in new[]
        {
            "CREATE CONSTRAINT memory_episode_id IF NOT EXISTS FOR (n:MemoryEpisode) REQUIRE n.id IS UNIQUE",
            "CREATE CONSTRAINT memory_entity_id IF NOT EXISTS FOR (n:MemoryEntity) REQUIRE n.id IS UNIQUE",
            "CREATE CONSTRAINT memory_claim_id IF NOT EXISTS FOR (n:MemoryClaim) REQUIRE n.id IS UNIQUE",
            "CREATE INDEX memory_partition IF NOT EXISTS FOR (n:MemoryEntity) ON (n.partitionKey)"
        }) await ExecuteAsync(query, null, cancellationToken);
    }

    public Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default) =>
        MergeNodeAsync("MemoryEpisode", episode.Id, episode.Partition.Key, JsonSerializer.Serialize(episode, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["content"] = episode.Content, ["idempotencyKey"] = episode.IdempotencyKey });

    public Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default) =>
        MergeNodeAsync("MemoryEntity", entity.Id, entity.Partition.Key, JsonSerializer.Serialize(entity, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["canonicalName"] = entity.CanonicalName.ToLowerInvariant(), ["type"] = entity.Type });

    public async Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync("MATCH (n:MemoryEntity {partitionKey:$partition,canonicalName:$name}) RETURN n.payload AS payload LIMIT 1",
            new { partition = partition.Key, name = canonicalName.ToLowerInvariant() }, cancellationToken);
        return result.Count == 0 ? null : JsonSerializer.Deserialize<MemoryEntity>((string)result[0]["payload"], JsonOptions);
    }

    public async Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default)
    {
        await MergeNodeAsync("MemoryClaim", claim.Id, claim.Partition.Key, JsonSerializer.Serialize(claim, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["predicate"] = claim.Predicate, ["value"] = claim.Value, ["validFrom"] = claim.ValidFrom.ToString("O"), ["validTo"] = claim.ValidTo?.ToString("O"), ["confirmation"] = (int)claim.Confirmation });
        await ExecuteAsync("MATCH (c:MemoryClaim {id:$id}),(e:MemoryEpisode {id:$episode}),(s:MemoryEntity {id:$subject}) MERGE (c)-[:DERIVED_FROM]->(e) MERGE (c)-[:SUBJECT]->(s)",
            new { id = claim.Id.ToString("D"), episode = claim.EpisodeId.ToString("D"), subject = claim.SubjectEntityId.ToString("D") }, cancellationToken);
        if (claim.ObjectEntityId is Guid objectId)
            await ExecuteAsync("MATCH (c:MemoryClaim {id:$id}),(o:MemoryEntity {id:$object}) MERGE (c)-[:OBJECT]->(o)", new { id = claim.Id.ToString("D"), @object = objectId.ToString("D") }, cancellationToken);
        return new MemoryWriteResult(claim.Id, true);
    }

    public async Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default)
    {
        await MergeNodeAsync("MemoryEdge", edge.Id, edge.Partition.Key, JsonSerializer.Serialize(edge, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["relationship"] = edge.Relationship, ["validFrom"] = edge.ValidFrom.ToString("O"), ["validTo"] = edge.ValidTo?.ToString("O") });
        await ExecuteAsync("MATCH (r:MemoryEdge {id:$id}),(f:MemoryEntity {id:$from}),(t:MemoryEntity {id:$to}),(e:MemoryEpisode {id:$episode}) MERGE (f)-[:FROM_EDGE]->(r) MERGE (r)-[:TO_ENTITY]->(t) MERGE (r)-[:DERIVED_FROM]->(e)",
            new { id = edge.Id.ToString("D"), from = edge.FromEntityId.ToString("D"), to = edge.ToEntityId.ToString("D"), episode = edge.EpisodeId.ToString("D") }, cancellationToken);
        return new MemoryWriteResult(edge.Id, true);
    }

    public Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default) =>
        MergeNodeAsync("MemoryBlock", block.Id, block.Partition.Key, JsonSerializer.Serialize(block, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["name"] = block.Name, ["content"] = block.Content, ["pinned"] = block.IsPinned });

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) =>
        MergeNodeAsync("ProceduralMemory", procedure.Id, procedure.Partition.Key, JsonSerializer.Serialize(procedure, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["name"] = procedure.Name, ["procedure"] = procedure.Procedure, ["confirmation"] = (int)procedure.Confirmation, ["validFrom"] = procedure.ValidFrom.ToString("O"), ["validTo"] = procedure.ValidTo?.ToString("O") });

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        MergeNodeAsync("MemoryEmbedding", embedding.Id, embedding.Partition.Key, JsonSerializer.Serialize(embedding, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["memoryId"] = embedding.MemoryId.ToString("D"), ["model"] = embedding.Model });

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default) =>
        await MergeNodeAsync("MemoryUse", use.Id, use.Partition.Key, JsonSerializer.Serialize(use, JsonOptions), cancellationToken,
            new Dictionary<string, object?> { ["memoryId"] = use.MemoryId.ToString("D"), ["outcome"] = (int)use.Outcome });

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        const string query = """
            MATCH (n) WHERE n.partitionKey=$partition AND
              ((n:MemoryEpisode AND toLower(n.content) CONTAINS toLower($query)) OR
               (n:MemoryClaim AND (toLower(coalesce(n.value,'')) CONTAINS toLower($query) OR toLower(n.predicate) CONTAINS toLower($query))) OR
               (n:MemoryBlock) OR
               (n:ProceduralMemory AND n.confirmation IN [0,2] AND toLower(n.procedure) CONTAINS toLower($query)))
            RETURN labels(n)[0] AS label,n.payload AS payload LIMIT $limit
            """;
        var records = await QueryAsync(query, new { partition = request.Partition.Key, query = request.Query, limit = request.Limit }, cancellationToken);
        var candidates = new List<MemoryCandidate>();
        foreach (var record in records)
        {
            var label = (string)record["label"];
            var payload = (string)record["payload"];
            switch (label)
            {
                case "MemoryEpisode":
                    var episode = JsonSerializer.Deserialize<MemoryEpisode>(payload, JsonOptions)!;
                    candidates.Add(new(episode.Id, MemoryLayer.Episodic, episode.Content, 1, MemoryTrustTier.External, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, episode.OccurredAt, episode.ExpiresAt, [episode.Id], "neo4j-text"));
                    break;
                case "MemoryClaim":
                    var claim = JsonSerializer.Deserialize<MemoryClaim>(payload, JsonOptions)!;
                    if (claim.ValidTo is null && (request.IncludePending || claim.Confirmation != MemoryConfirmationState.Pending))
                        candidates.Add(new(claim.Id, MemoryLayer.Semantic, $"{claim.Predicate} {claim.Value}", claim.Confidence * claim.Importance, claim.Trust, claim.Confirmation, claim.Sensitivity, claim.ValidFrom, claim.ValidTo, [claim.EpisodeId], "neo4j-graph"));
                    break;
                case "MemoryBlock":
                    var block = JsonSerializer.Deserialize<MemoryBlock>(payload, JsonOptions)!;
                    candidates.Add(new(block.Id, MemoryLayer.Core, block.Content, block.IsPinned ? 2 : 1, block.Trust, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, null, null, [], "neo4j-core"));
                    break;
                case "ProceduralMemory":
                    var procedure = JsonSerializer.Deserialize<ProceduralMemory>(payload, JsonOptions)!;
                    candidates.Add(new(procedure.Id, MemoryLayer.Procedural, procedure.Procedure, 1, procedure.Trust, procedure.Confirmation, MemorySensitivity.Internal, procedure.ValidFrom, procedure.ValidTo, [procedure.EpisodeId], "neo4j-procedure"));
                    break;
            }
        }
        return candidates;
    }

    public async Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await WriteClaimAsync(claim with { ValidTo = validTo }, cancellationToken);
        await ExecuteAsync("MATCH (old:MemoryClaim {id:$old}),(current:MemoryClaim {id:$current}) MERGE (current)-[:SUPERSEDES]->(old)", new { old = claimId.ToString("D"), current = supersededByClaimId.ToString("D") }, cancellationToken);
    }

    public async Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        var records = await QueryAsync("MATCH (n:MemoryClaim {id:$id}) RETURN n.payload AS payload LIMIT 1", new { id = claimId.ToString("D") }, cancellationToken);
        return records.Count == 0 ? null : JsonSerializer.Deserialize<MemoryClaim>((string)records[0]["payload"], JsonOptions);
    }

    public async Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await WriteClaimAsync(claim with { Confirmation = confirmation }, cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default) =>
        await ListAsync<MemoryClaim>("MemoryClaim", partition, cancellationToken);

    public async Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => new(
        "1.0", await ListAsync<MemoryEpisode>("MemoryEpisode", partition, cancellationToken),
        await ListAsync<MemoryEntity>("MemoryEntity", partition, cancellationToken), await ListAsync<MemoryClaim>("MemoryClaim", partition, cancellationToken),
        await ListAsync<MemoryEdge>("MemoryEdge", partition, cancellationToken), await ListAsync<MemoryBlock>("MemoryBlock", partition, cancellationToken),
        await ListAsync<ProceduralMemory>("ProceduralMemory", partition, cancellationToken),
        await ListAsync<MemoryEmbedding>("MemoryEmbedding", partition, cancellationToken));

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default) =>
        await ExecuteAsync("MATCH (n {partitionKey:$partition}) DETACH DELETE n", new { partition = partition.Key }, cancellationToken);

    public async ValueTask DisposeAsync() => await _driver.DisposeAsync();

    private async Task<MemoryWriteResult> MergeNodeAsync(string label, Guid id, string partition, string payload, CancellationToken cancellationToken, IReadOnlyDictionary<string, object?>? properties = null)
    {
        var parameters = new Dictionary<string, object?> { ["id"] = id.ToString("D"), ["partition"] = partition, ["payload"] = payload };
        if (properties is not null) foreach (var pair in properties) parameters[pair.Key] = pair.Value;
        var assignments = properties is null ? string.Empty : "," + string.Join(',', properties.Keys.Select(key => $"n.{key}=${key}"));
        await ExecuteAsync($"MERGE (n:{label} {{id:$id}}) SET n.partitionKey=$partition,n.payload=$payload{assignments}", parameters, cancellationToken);
        return new MemoryWriteResult(id, true);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string label, MemoryPartition partition, CancellationToken cancellationToken)
    {
        var records = await QueryAsync($"MATCH (n:{label} {{partitionKey:$partition}}) RETURN n.payload AS payload", new { partition = partition.Key }, cancellationToken);
        return records.Select(record => JsonSerializer.Deserialize<T>((string)record["payload"], JsonOptions)!).ToList();
    }

    private async Task ExecuteAsync(string query, object? parameters, CancellationToken cancellationToken)
    {
        var executable = _driver.ExecutableQuery(query).WithConfig(new QueryConfig(database: _database));
        if (parameters is not null) executable = executable.WithParameters(parameters);
        await executable.ExecuteAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<IRecord>> QueryAsync(string query, object? parameters, CancellationToken cancellationToken)
    {
        var executable = _driver.ExecutableQuery(query).WithConfig(new QueryConfig(database: _database));
        if (parameters is not null) executable = executable.WithParameters(parameters);
        var result = await executable.ExecuteAsync(cancellationToken);
        return result.Result.ToList();
    }
}
