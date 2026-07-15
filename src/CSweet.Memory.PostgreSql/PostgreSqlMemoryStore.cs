using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.Memory;

public sealed class PostgreSqlMemoryStore : IMemoryStore, IKnowledgeTransferStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PostgreSqlMemoryStore(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    public MemoryStoreCapabilities Capabilities => MemoryStoreCapabilities.Transactions |
        MemoryStoreCapabilities.FullText | MemoryStoreCapabilities.RecursiveTraversal |
        MemoryStoreCapabilities.TemporalQueries | MemoryStoreCapabilities.BulkOperations |
        MemoryStoreCapabilities.ChangeHistory;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var command = _dataSource.CreateCommand(Schema);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        const string sql = """
            INSERT INTO csweet_memory_episodes(id,partition_key,idempotency_key,content,occurred_at,expires_at,payload)
            VALUES(@id,@partition,@key,@content,@occurred,@expires,@payload::jsonb)
            ON CONFLICT(partition_key,idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
            RETURNING id
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", episode.Id);
        command.Parameters.AddWithValue("partition", episode.Partition.Key);
        command.Parameters.AddWithValue("key", Db(episode.IdempotencyKey));
        command.Parameters.AddWithValue("content", episode.Content);
        command.Parameters.AddWithValue("occurred", episode.OccurredAt);
        command.Parameters.AddWithValue("expires", Db(episode.ExpiresAt));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(episode, JsonOptions));
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is Guid id) return new MemoryWriteResult(id, true);
        await using var existing = _dataSource.CreateCommand("SELECT id FROM csweet_memory_episodes WHERE partition_key=@partition AND idempotency_key=@key");
        existing.Parameters.AddWithValue("partition", episode.Partition.Key);
        existing.Parameters.AddWithValue("key", episode.IdempotencyKey!);
        return new MemoryWriteResult((Guid)(await existing.ExecuteScalarAsync(cancellationToken))!, false, "Idempotent replay.");
    }

    public async Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(entity.ApplicationKey))
        {
            var identified = await FindEntityByApplicationKeyAsync(entity.Partition, entity.ApplicationKey, cancellationToken);
            if (identified is not null)
            {
                entity = entity with { Id = identified.Id, CreatedAt = identified.CreatedAt };
                await using var update = _dataSource.CreateCommand("UPDATE csweet_memory_entities SET canonical_name=@name,payload=@payload::jsonb WHERE id=@id");
                update.Parameters.AddWithValue("name", entity.CanonicalName);
                update.Parameters.AddWithValue("payload", JsonSerializer.Serialize(entity, JsonOptions));
                update.Parameters.AddWithValue("id", entity.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
                return new MemoryWriteResult(entity.Id, false, "Updated by authoritative application key.");
            }
        }
        const string sql = """
            INSERT INTO csweet_memory_entities(id,partition_key,canonical_name,application_key,payload)
            VALUES(@id,@partition,@name,@applicationKey,@payload::jsonb)
            ON CONFLICT(partition_key,lower(canonical_name)) DO UPDATE SET application_key=excluded.application_key,payload=excluded.payload
            RETURNING id
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", entity.Id);
        command.Parameters.AddWithValue("partition", entity.Partition.Key);
        command.Parameters.AddWithValue("name", entity.CanonicalName);
        command.Parameters.AddWithValue("applicationKey", Db(entity.ApplicationKey));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(entity, JsonOptions));
        var id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new MemoryWriteResult(id, id == entity.Id);
    }

    public async Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition AND lower(canonical_name)=lower(@name) LIMIT 1");
        command.Parameters.AddWithValue("partition", partition.Key);
        command.Parameters.AddWithValue("name", canonicalName);
        var exact = Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
        if (exact is not null) return exact;
        await foreach (var json in QueryPayloadsAsync("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition", partition.Key, cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<MemoryEntity>(json, JsonOptions)!;
            if (entity.Aliases.Contains(canonicalName, StringComparer.OrdinalIgnoreCase)) return entity;
        }
        return null;
    }

    public async Task<MemoryEntity?> FindEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition AND application_key=@key LIMIT 1");
        command.Parameters.AddWithValue("partition", partition.Key);
        command.Parameters.AddWithValue("key", applicationKey);
        return Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default) =>
        InsertAsync("csweet_memory_claims", claim.Id, claim.Partition.Key, JsonSerializer.Serialize(claim, JsonOptions), cancellationToken,
            ("episode_id", claim.EpisodeId), ("subject_id", claim.SubjectEntityId), ("predicate", claim.Predicate),
            ("value", claim.Value), ("confirmation", (int)claim.Confirmation), ("valid_from", claim.ValidFrom), ("valid_to", claim.ValidTo));

    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default) =>
        InsertAsync("csweet_memory_edges", edge.Id, edge.Partition.Key, JsonSerializer.Serialize(edge, JsonOptions), cancellationToken,
            ("episode_id", edge.EpisodeId), ("from_id", edge.FromEntityId), ("relationship", edge.Relationship),
            ("to_id", edge.ToEntityId), ("valid_from", edge.ValidFrom), ("valid_to", edge.ValidTo));

    public async Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        const string sql = """
            INSERT INTO csweet_memory_blocks(id,partition_key,name,pinned,payload) VALUES(@id,@partition,@name,@pinned,@payload::jsonb)
            ON CONFLICT(partition_key,name) DO UPDATE SET id=excluded.id,pinned=excluded.pinned,payload=excluded.payload RETURNING id
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", block.Id);
        command.Parameters.AddWithValue("partition", block.Partition.Key);
        command.Parameters.AddWithValue("name", block.Name);
        command.Parameters.AddWithValue("pinned", block.IsPinned);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(block, JsonOptions));
        var id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new MemoryWriteResult(id, id == block.Id);
    }

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) =>
        InsertAsync("csweet_memory_procedures", procedure.Id, procedure.Partition.Key, JsonSerializer.Serialize(procedure, JsonOptions), cancellationToken,
            ("episode_id", procedure.EpisodeId), ("name", procedure.Name), ("confirmation", (int)procedure.Confirmation),
            ("valid_from", procedure.ValidFrom), ("valid_to", procedure.ValidTo));

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        InsertAsync("csweet_memory_embeddings", embedding.Id, embedding.Partition.Key, JsonSerializer.Serialize(embedding, JsonOptions), cancellationToken,
            ("memory_id", embedding.MemoryId), ("layer", (int)embedding.Layer));

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = _dataSource.CreateCommand("INSERT INTO csweet_memory_uses(id,partition_key,memory_id,outcome,payload) VALUES(@id,@partition,@memory,@outcome,@payload::jsonb)");
        command.Parameters.AddWithValue("id", use.Id);
        command.Parameters.AddWithValue("partition", use.Partition.Key);
        command.Parameters.AddWithValue("memory", use.MemoryId);
        command.Parameters.AddWithValue("outcome", (int)use.Outcome);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(use, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var results = new List<MemoryCandidate>();
        var asOf = request.AsOf ?? DateTimeOffset.UtcNow;
        if (Included(request, MemoryLayer.Core))
        {
            await foreach (var json in QueryPayloadsAsync("SELECT payload::text FROM csweet_memory_blocks WHERE partition_key=@partition ORDER BY pinned DESC", request.Partition.Key, cancellationToken))
            {
                var block = JsonSerializer.Deserialize<MemoryBlock>(json, JsonOptions)!;
                results.Add(new(block.Id, MemoryLayer.Core, block.Content, block.IsPinned ? 2 : 1, block.Trust,
                    MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, null, null, [], "core"));
            }
        }
        if (Included(request, MemoryLayer.Episodic))
        {
            const string sql = """
                SELECT payload::text,ts_rank_cd(search_vector,plainto_tsquery('simple',@query)) score FROM csweet_memory_episodes
                WHERE partition_key=@partition AND search_vector @@ plainto_tsquery('simple',@query) AND (expires_at IS NULL OR expires_at>@asOf)
                ORDER BY score DESC LIMIT @limit
                """;
            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.Key);
            command.Parameters.AddWithValue("query", request.Query);
            command.Parameters.AddWithValue("asOf", asOf);
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), JsonOptions)!;
                results.Add(new(episode.Id, MemoryLayer.Episodic, episode.Content, reader.GetDouble(1), SourceTrust(episode.Source.Type),
                    MemoryConfirmationState.NotRequired, episode.Sensitivity, episode.OccurredAt, episode.ExpiresAt, [episode.Id], "fulltext"));
            }
        }
        if (Included(request, MemoryLayer.Semantic))
        {
            const string sql = """
                SELECT c.payload::text,e.canonical_name FROM csweet_memory_claims c JOIN csweet_memory_entities e ON e.id=c.subject_id
                WHERE c.partition_key=@partition AND c.valid_from<=@asOf AND (@superseded OR c.valid_to IS NULL OR c.valid_to>@asOf)
                  AND (@pending OR c.confirmation<>1) AND (c.predicate ILIKE @query OR c.value ILIKE @query OR e.canonical_name ILIKE @query) LIMIT @limit
                """;
            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.Key);
            command.Parameters.AddWithValue("asOf", asOf);
            command.Parameters.AddWithValue("superseded", request.IncludeSuperseded);
            command.Parameters.AddWithValue("pending", request.IncludePending);
            command.Parameters.AddWithValue("query", $"%{request.Query}%");
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var claim = JsonSerializer.Deserialize<MemoryClaim>(reader.GetString(0), JsonOptions)!;
                results.Add(new(claim.Id, MemoryLayer.Semantic, $"{reader.GetString(1)} {claim.Predicate} {claim.Value ?? claim.ObjectEntityId?.ToString()}",
                    claim.Confidence * claim.Importance, claim.Trust, claim.Confirmation, claim.Sensitivity,
                    claim.ValidFrom, claim.ValidTo, [claim.EpisodeId], "semantic"));
            }

            const string graphSql = """
                WITH RECURSIVE roots(id) AS (
                    SELECT id FROM csweet_memory_entities WHERE partition_key=@partition AND canonical_name ILIKE @query
                ), walk(edge_id,from_id,to_id,depth) AS (
                    SELECT e.id,e.from_id,e.to_id,1 FROM csweet_memory_edges e JOIN roots r ON e.from_id=r.id
                    WHERE e.partition_key=@partition AND (e.valid_to IS NULL OR e.valid_to>@asOf)
                    UNION
                    SELECT e.id,e.from_id,e.to_id,w.depth+1 FROM csweet_memory_edges e JOIN walk w ON e.from_id=w.to_id
                    WHERE e.partition_key=@partition AND w.depth<3 AND (e.valid_to IS NULL OR e.valid_to>@asOf)
                )
                SELECT edge.payload::text,source.canonical_name,target.canonical_name FROM walk w
                JOIN csweet_memory_edges edge ON edge.id=w.edge_id JOIN csweet_memory_entities source ON source.id=w.from_id
                JOIN csweet_memory_entities target ON target.id=w.to_id LIMIT @limit
                """;
            await using var graph = _dataSource.CreateCommand(graphSql);
            graph.Parameters.AddWithValue("partition", request.Partition.Key);
            graph.Parameters.AddWithValue("query", $"%{request.Query}%");
            graph.Parameters.AddWithValue("asOf", asOf);
            graph.Parameters.AddWithValue("limit", request.Limit);
            await using var graphReader = await graph.ExecuteReaderAsync(cancellationToken);
            while (await graphReader.ReadAsync(cancellationToken))
            {
                var edge = JsonSerializer.Deserialize<MemoryEdge>(graphReader.GetString(0), JsonOptions)!;
                results.Add(new(edge.Id, MemoryLayer.Semantic, $"{graphReader.GetString(1)} {edge.Relationship} {graphReader.GetString(2)}",
                    edge.Confidence, edge.Trust, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
                    edge.ValidFrom, edge.ValidTo, [edge.EpisodeId], "graph"));
            }
        }

        if (request.Embedding is { Count: > 0 } && Included(request, MemoryLayer.Episodic))
        {
            const string vectorSql = "SELECT embedding.payload::text,episode.payload::text FROM csweet_memory_embeddings embedding JOIN csweet_memory_episodes episode ON episode.id=embedding.memory_id WHERE embedding.partition_key=@partition";
            await using var vector = _dataSource.CreateCommand(vectorSql);
            vector.Parameters.AddWithValue("partition", request.Partition.Key);
            var vectorCandidates = new List<MemoryCandidate>();
            await using var vectorReader = await vector.ExecuteReaderAsync(cancellationToken);
            while (await vectorReader.ReadAsync(cancellationToken))
            {
                var embedding = JsonSerializer.Deserialize<MemoryEmbedding>(vectorReader.GetString(0), JsonOptions)!;
                if (embedding.Vector.Count != request.Embedding.Count) continue;
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(vectorReader.GetString(1), JsonOptions)!;
                vectorCandidates.Add(new(episode.Id, MemoryLayer.Episodic, episode.Content, CosineSimilarity(request.Embedding, embedding.Vector),
                    SourceTrust(episode.Source.Type), MemoryConfirmationState.NotRequired, episode.Sensitivity,
                    episode.OccurredAt, episode.ExpiresAt, [episode.Id], "vector"));
            }
            results.AddRange(vectorCandidates.OrderByDescending(candidate => candidate.Score).Take(request.Limit));
        }
        if (Included(request, MemoryLayer.Procedural))
        {
            const string sql = "SELECT payload::text FROM csweet_memory_procedures WHERE partition_key=@partition AND confirmation IN (0,2) AND valid_from<=@asOf AND (valid_to IS NULL OR valid_to>@asOf) AND (name ILIKE @query OR payload::text ILIKE @query) LIMIT @limit";
            await using var command = _dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.Key);
            command.Parameters.AddWithValue("asOf", asOf);
            command.Parameters.AddWithValue("query", $"%{request.Query}%");
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var procedure = JsonSerializer.Deserialize<ProceduralMemory>(reader.GetString(0), JsonOptions)!;
                results.Add(new(procedure.Id, MemoryLayer.Procedural, procedure.Procedure, 1, procedure.Trust, procedure.Confirmation,
                    MemorySensitivity.Internal, procedure.ValidFrom, procedure.ValidTo, [procedure.EpisodeId], "procedure"));
            }
        }
        return results;
    }

    public async Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await UpdateClaimAsync(claim with { ValidTo = validTo }, cancellationToken);
    }

    public async Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM csweet_memory_claims WHERE id=@id");
        command.Parameters.AddWithValue("id", claimId);
        return Deserialize<MemoryClaim>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await UpdateClaimAsync(claim with { Confirmation = confirmation }, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => ListAsync<MemoryClaim>("csweet_memory_claims", partition, cancellationToken);

    public async Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => new(
        "1.0", await ListAsync<MemoryEpisode>("csweet_memory_episodes", partition, cancellationToken),
        await ListAsync<MemoryEntity>("csweet_memory_entities", partition, cancellationToken),
        await ListAsync<MemoryClaim>("csweet_memory_claims", partition, cancellationToken),
        await ListAsync<MemoryEdge>("csweet_memory_edges", partition, cancellationToken),
        await ListAsync<MemoryBlock>("csweet_memory_blocks", partition, cancellationToken),
        await ListAsync<ProceduralMemory>("csweet_memory_procedures", partition, cancellationToken),
        await ListAsync<MemoryEmbedding>("csweet_memory_embeddings", partition, cancellationToken));

    public async Task WriteKnowledgeTransferAsync(KnowledgeTransferPackage package, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        const string sql = """
            INSERT INTO csweet_memory_transfers(id,tenant_id,source_employee_id,target_employee_id,status,created_at,payload)
            VALUES(@id,@tenant,@source,@target,@status,@created,@payload::jsonb)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", package.Id);
        command.Parameters.AddWithValue("tenant", package.TenantId);
        command.Parameters.AddWithValue("source", package.SourceEmployeeId);
        command.Parameters.AddWithValue("target", package.TargetEmployeeId);
        command.Parameters.AddWithValue("status", (int)package.Status);
        command.Parameters.AddWithValue("created", package.CreatedAt);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(package, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<KnowledgeTransferPackage?> GetKnowledgeTransferAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM csweet_memory_transfers WHERE id=@id");
        command.Parameters.AddWithValue("id", packageId);
        return Deserialize<KnowledgeTransferPackage>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in Tables.AsEnumerable().Reverse())
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE partition_key=@partition", connection, transaction);
            command.Parameters.AddWithValue("partition", partition.Key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _initializeLock.Dispose();
        await _dataSource.DisposeAsync();
    }

    private async Task<MemoryWriteResult> InsertAsync(string table, Guid id, string partition, string payload, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await InitializeAsync(cancellationToken);
        var columns = string.Join(',', values.Select(value => value.Name));
        var parameters = string.Join(',', values.Select(value => '@' + value.Name));
        await using var command = _dataSource.CreateCommand($"INSERT INTO {table}(id,partition_key,{columns},payload) VALUES(@id,@partition,{parameters},@payload::jsonb)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("partition", partition);
        command.Parameters.AddWithValue("payload", payload);
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, Db(value.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new MemoryWriteResult(id, true);
    }

    private async Task UpdateClaimAsync(MemoryClaim claim, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("UPDATE csweet_memory_claims SET confirmation=@confirmation,valid_to=@validTo,payload=@payload::jsonb WHERE id=@id");
        command.Parameters.AddWithValue("confirmation", (int)claim.Confirmation);
        command.Parameters.AddWithValue("validTo", Db(claim.ValidTo));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(claim, JsonOptions));
        command.Parameters.AddWithValue("id", claim.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string table, MemoryPartition partition, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var results = new List<T>();
        await foreach (var json in QueryPayloadsAsync($"SELECT payload::text FROM {table} WHERE partition_key=@partition", partition.Key, cancellationToken))
            results.Add(JsonSerializer.Deserialize<T>(json, JsonOptions)!);
        return results;
    }

    private async IAsyncEnumerable<string> QueryPayloadsAsync(string sql, string partition, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("partition", partition);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) yield return reader.GetString(0);
    }

    private static bool Included(MemorySearchRequest request, MemoryLayer layer) => request.Layers is null || request.Layers.Contains(layer);
    private static object Db(object? value) => value ?? DBNull.Value;
    private static T? Deserialize<T>(object? value) => value is string json ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;
    private static MemoryTrustTier SourceTrust(string sourceType) => sourceType.Equals("application", StringComparison.OrdinalIgnoreCase) ? MemoryTrustTier.Authoritative : sourceType.Equals("user", StringComparison.OrdinalIgnoreCase) ? MemoryTrustTier.UnconfirmedUser : MemoryTrustTier.External;
    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }
        return leftLength == 0 || rightLength == 0 ? 0 : dot / (Math.Sqrt(leftLength) * Math.Sqrt(rightLength));
    }
    private static readonly string[] Tables = ["csweet_memory_episodes", "csweet_memory_entities", "csweet_memory_claims", "csweet_memory_edges", "csweet_memory_blocks", "csweet_memory_procedures", "csweet_memory_embeddings", "csweet_memory_uses"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS csweet_memory_episodes(id uuid PRIMARY KEY,partition_key text NOT NULL,idempotency_key text,content text NOT NULL,occurred_at timestamptz NOT NULL,expires_at timestamptz,payload jsonb NOT NULL,search_vector tsvector GENERATED ALWAYS AS (to_tsvector('simple',content)) STORED);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_csweet_memory_episode_idempotency ON csweet_memory_episodes(partition_key,idempotency_key) WHERE idempotency_key IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_episode_search ON csweet_memory_episodes USING GIN(search_vector);
        CREATE TABLE IF NOT EXISTS csweet_memory_entities(id uuid PRIMARY KEY,partition_key text NOT NULL,canonical_name text NOT NULL,application_key text,payload jsonb NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_csweet_memory_entity_name ON csweet_memory_entities(partition_key,lower(canonical_name));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_csweet_memory_entity_application_key ON csweet_memory_entities(partition_key,application_key) WHERE application_key IS NOT NULL;
        CREATE TABLE IF NOT EXISTS csweet_memory_claims(id uuid PRIMARY KEY,partition_key text NOT NULL,episode_id uuid NOT NULL,subject_id uuid NOT NULL,predicate text NOT NULL,value text,confirmation integer NOT NULL,valid_from timestamptz NOT NULL,valid_to timestamptz,payload jsonb NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_claim_lookup ON csweet_memory_claims(partition_key,subject_id,predicate,valid_to);
        CREATE TABLE IF NOT EXISTS csweet_memory_edges(id uuid PRIMARY KEY,partition_key text NOT NULL,episode_id uuid NOT NULL,from_id uuid NOT NULL,relationship text NOT NULL,to_id uuid NOT NULL,valid_from timestamptz NOT NULL,valid_to timestamptz,payload jsonb NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_edge_traversal ON csweet_memory_edges(partition_key,from_id,relationship,valid_to);
        CREATE TABLE IF NOT EXISTS csweet_memory_blocks(id uuid PRIMARY KEY,partition_key text NOT NULL,name text NOT NULL,pinned boolean NOT NULL,payload jsonb NOT NULL,UNIQUE(partition_key,name));
        CREATE TABLE IF NOT EXISTS csweet_memory_procedures(id uuid PRIMARY KEY,partition_key text NOT NULL,episode_id uuid NOT NULL,name text NOT NULL,confirmation integer NOT NULL,valid_from timestamptz NOT NULL,valid_to timestamptz,payload jsonb NOT NULL);
        CREATE TABLE IF NOT EXISTS csweet_memory_embeddings(id uuid PRIMARY KEY,partition_key text NOT NULL,memory_id uuid NOT NULL,layer integer NOT NULL,payload jsonb NOT NULL);
        CREATE TABLE IF NOT EXISTS csweet_memory_uses(id uuid PRIMARY KEY,partition_key text NOT NULL,memory_id uuid NOT NULL,outcome integer NOT NULL,payload jsonb NOT NULL);
        CREATE TABLE IF NOT EXISTS csweet_memory_transfers(id uuid PRIMARY KEY,tenant_id text NOT NULL,source_employee_id text NOT NULL,target_employee_id text NOT NULL,status integer NOT NULL,created_at timestamptz NOT NULL,payload jsonb NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_transfer_employees ON csweet_memory_transfers(tenant_id,source_employee_id,target_employee_id,status);
        """;
}
