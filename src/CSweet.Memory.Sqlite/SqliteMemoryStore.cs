using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CSweet.Memory;

public sealed class SqliteMemoryStore : IMemoryStore, IKnowledgeTransferStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteMemoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

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
            await using var connection = await OpenAsync(cancellationToken, initialize: false);
            await using var command = connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(episode.IdempotencyKey))
        {
            await using var existing = connection.CreateCommand();
            existing.CommandText = "SELECT id FROM memory_episodes WHERE partition_key=$partition AND idempotency_key=$key LIMIT 1";
            existing.Parameters.AddWithValue("$partition", episode.Partition.Key);
            existing.Parameters.AddWithValue("$key", episode.IdempotencyKey);
            var id = await existing.ExecuteScalarAsync(cancellationToken);
            if (id is string value) return new MemoryWriteResult(Guid.Parse(value), false, "Idempotent replay.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO memory_episodes(id, partition_key, tenant_id, application_id, agent_id, user_id, conversation_id, custom_namespace, scope, content, idempotency_key, occurred_at, expires_at, legal_hold, payload)
            VALUES($id,$partition,$tenant,$application,$agent,$user,$conversation,$custom,$scope,$content,$key,$occurred,$expires,$legal,$payload)
            """;
        BindPartition(command, episode.Partition);
        command.Parameters.AddWithValue("$id", episode.Id.ToString("D"));
        command.Parameters.AddWithValue("$scope", (int)episode.Scope);
        command.Parameters.AddWithValue("$content", episode.Content);
        command.Parameters.AddWithValue("$key", Db(episode.IdempotencyKey));
        command.Parameters.AddWithValue("$occurred", episode.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", Db(episode.ExpiresAt?.ToString("O")));
        command.Parameters.AddWithValue("$legal", episode.LegalHold ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(episode, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var fts = connection.CreateCommand();
        fts.Transaction = (SqliteTransaction)transaction;
        fts.CommandText = "INSERT INTO memory_episodes_fts(id, content) VALUES($id,$content)";
        fts.Parameters.AddWithValue("$id", episode.Id.ToString("D"));
        fts.Parameters.AddWithValue("$content", episode.Content);
        await fts.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MemoryWriteResult(episode.Id, true);
    }

    public async Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(entity.ApplicationKey))
        {
            var identified = await FindEntityByApplicationKeyAsync(entity.Partition, entity.ApplicationKey, cancellationToken);
            if (identified is not null)
            {
                entity = entity with { Id = identified.Id, CreatedAt = identified.CreatedAt };
                await using var updateConnection = await OpenAsync(cancellationToken);
                await using var update = updateConnection.CreateCommand();
                update.CommandText = "UPDATE memory_entities SET canonical_name=$name,payload=$payload WHERE id=$id";
                update.Parameters.AddWithValue("$name", entity.CanonicalName);
                update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entity, JsonOptions));
                update.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken);
                return new MemoryWriteResult(entity.Id, false, "Updated by authoritative application key.");
            }
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_entities(id, partition_key, canonical_name, application_key, payload)
            VALUES($id,$partition,$name,$applicationKey,$payload)
            ON CONFLICT(partition_key, canonical_name) DO UPDATE SET payload=excluded.payload, application_key=excluded.application_key
            """;
        command.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", entity.Partition.Key);
        command.Parameters.AddWithValue("$name", entity.CanonicalName);
        command.Parameters.AddWithValue("$applicationKey", Db(entity.ApplicationKey));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entity, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        var stored = await FindEntityAsync(entity.Partition, entity.CanonicalName, cancellationToken);
        return new MemoryWriteResult(stored?.Id ?? entity.Id, stored?.Id == entity.Id);
    }

    public async Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition AND canonical_name=$name COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$partition", partition.Key);
        command.Parameters.AddWithValue("$name", canonicalName);
        var exact = Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
        if (exact is not null) return exact;
        await using var aliases = connection.CreateCommand();
        aliases.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition";
        aliases.Parameters.AddWithValue("$partition", partition.Key);
        await using var reader = await aliases.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(0), JsonOptions)!;
            if (entity.Aliases.Contains(canonicalName, StringComparer.OrdinalIgnoreCase)) return entity;
        }
        return null;
    }

    public async Task<MemoryEntity?> FindEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition AND application_key=$key LIMIT 1";
        command.Parameters.AddWithValue("$partition", partition.Key);
        command.Parameters.AddWithValue("$key", applicationKey);
        return Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default) =>
        InsertPayloadAsync("memory_claims", claim.Id, claim.Partition, JsonSerializer.Serialize(claim, JsonOptions), cancellationToken,
            ("episode_id", claim.EpisodeId.ToString("D")), ("subject_id", claim.SubjectEntityId.ToString("D")),
            ("predicate", claim.Predicate), ("value", claim.Value), ("confirmation", ((int)claim.Confirmation).ToString(CultureInfo.InvariantCulture)),
            ("valid_from", claim.ValidFrom.ToString("O")), ("valid_to", claim.ValidTo?.ToString("O")));

    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default) =>
        InsertPayloadAsync("memory_edges", edge.Id, edge.Partition, JsonSerializer.Serialize(edge, JsonOptions), cancellationToken,
            ("episode_id", edge.EpisodeId.ToString("D")), ("from_id", edge.FromEntityId.ToString("D")),
            ("relationship", edge.Relationship), ("to_id", edge.ToEntityId.ToString("D")),
            ("valid_from", edge.ValidFrom.ToString("O")), ("valid_to", edge.ValidTo?.ToString("O")));

    public async Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_blocks(id, partition_key, name, pinned, payload) VALUES($id,$partition,$name,$pinned,$payload)
            ON CONFLICT(partition_key,name) DO UPDATE SET id=excluded.id,pinned=excluded.pinned,payload=excluded.payload
            """;
        command.Parameters.AddWithValue("$id", block.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", block.Partition.Key);
        command.Parameters.AddWithValue("$name", block.Name);
        command.Parameters.AddWithValue("$pinned", block.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(block, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new MemoryWriteResult(block.Id, true);
    }

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) =>
        InsertPayloadAsync("memory_procedures", procedure.Id, procedure.Partition, JsonSerializer.Serialize(procedure, JsonOptions), cancellationToken,
            ("episode_id", procedure.EpisodeId.ToString("D")), ("name", procedure.Name),
            ("confirmation", ((int)procedure.Confirmation).ToString(CultureInfo.InvariantCulture)),
            ("valid_from", procedure.ValidFrom.ToString("O")), ("valid_to", procedure.ValidTo?.ToString("O")));

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        InsertPayloadAsync("memory_embeddings", embedding.Id, embedding.Partition, JsonSerializer.Serialize(embedding, JsonOptions), cancellationToken,
            ("memory_id", embedding.MemoryId.ToString("D")), ("layer", ((int)embedding.Layer).ToString(CultureInfo.InvariantCulture)));

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO memory_uses(id,partition_key,memory_id,outcome,payload) VALUES($id,$partition,$memory,$outcome,$payload)";
        command.Parameters.AddWithValue("$id", use.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", use.Partition.Key);
        command.Parameters.AddWithValue("$memory", use.MemoryId.ToString("D"));
        command.Parameters.AddWithValue("$outcome", (int)use.Outcome);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(use, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        var results = new List<MemoryCandidate>();
        await using var connection = await OpenAsync(cancellationToken);
        var now = (request.AsOf ?? DateTimeOffset.UtcNow).ToString("O");
        if (Included(request, MemoryLayer.Core))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM memory_blocks WHERE partition_key=$partition ORDER BY pinned DESC";
            command.Parameters.AddWithValue("$partition", request.Partition.Key);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var block = JsonSerializer.Deserialize<MemoryBlock>(reader.GetString(0), JsonOptions)!;
                results.Add(new MemoryCandidate(block.Id, MemoryLayer.Core, block.Content, block.IsPinned ? 2 : 1,
                    block.Trust, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, null, null, [], "core"));
            }
        }
        if (Included(request, MemoryLayer.Episodic))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.payload FROM memory_episodes e JOIN memory_episodes_fts f ON f.id=e.id
                WHERE e.partition_key=$partition AND f.content MATCH $query AND (e.expires_at IS NULL OR e.expires_at>$now)
                ORDER BY bm25(memory_episodes_fts) LIMIT $limit
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.Key);
            command.Parameters.AddWithValue("$query", ToFtsQuery(request.Query));
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var score = 1d;
            while (await reader.ReadAsync(cancellationToken))
            {
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), JsonOptions)!;
                results.Add(new MemoryCandidate(episode.Id, MemoryLayer.Episodic, episode.Content, score,
                    SourceTrust(episode.Source.Type), MemoryConfirmationState.NotRequired, episode.Sensitivity,
                    episode.OccurredAt, episode.ExpiresAt, [episode.Id], "fulltext"));
                score = Math.Max(0.1, score - 0.02);
            }
        }
        if (Included(request, MemoryLayer.Semantic))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.payload,e.canonical_name FROM memory_claims c JOIN memory_entities e ON e.id=c.subject_id
                WHERE c.partition_key=$partition AND (c.valid_from<=$now) AND ($superseded=1 OR c.valid_to IS NULL OR c.valid_to>$now)
                  AND ($pending=1 OR c.confirmation<>1) AND (c.predicate LIKE $query OR c.value LIKE $query OR e.canonical_name LIKE $query)
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.Key);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$superseded", request.IncludeSuperseded ? 1 : 0);
            command.Parameters.AddWithValue("$pending", request.IncludePending ? 1 : 0);
            command.Parameters.AddWithValue("$query", $"%{request.Query}%");
            command.Parameters.AddWithValue("$limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var claim = JsonSerializer.Deserialize<MemoryClaim>(reader.GetString(0), JsonOptions)!;
                var content = $"{reader.GetString(1)} {claim.Predicate} {claim.Value ?? claim.ObjectEntityId?.ToString()}";
                results.Add(new MemoryCandidate(claim.Id, MemoryLayer.Semantic, content, claim.Confidence * claim.Importance,
                    claim.Trust, claim.Confirmation, claim.Sensitivity, claim.ValidFrom, claim.ValidTo, [claim.EpisodeId], "semantic"));
            }

            await using var graph = connection.CreateCommand();
            graph.CommandText = """
                WITH RECURSIVE roots(id) AS (
                    SELECT id FROM memory_entities WHERE partition_key=$partition AND canonical_name LIKE $query
                ), walk(edge_id,from_id,to_id,depth) AS (
                    SELECT e.id,e.from_id,e.to_id,1 FROM memory_edges e JOIN roots r ON e.from_id=r.id
                    WHERE e.partition_key=$partition AND (e.valid_to IS NULL OR e.valid_to>$now)
                    UNION
                    SELECT e.id,e.from_id,e.to_id,w.depth+1 FROM memory_edges e JOIN walk w ON e.from_id=w.to_id
                    WHERE e.partition_key=$partition AND w.depth<3 AND (e.valid_to IS NULL OR e.valid_to>$now)
                )
                SELECT edge.payload,source.canonical_name,target.canonical_name FROM walk w
                JOIN memory_edges edge ON edge.id=w.edge_id
                JOIN memory_entities source ON source.id=w.from_id
                JOIN memory_entities target ON target.id=w.to_id
                LIMIT $limit
                """;
            graph.Parameters.AddWithValue("$partition", request.Partition.Key);
            graph.Parameters.AddWithValue("$query", $"%{request.Query}%");
            graph.Parameters.AddWithValue("$now", now);
            graph.Parameters.AddWithValue("$limit", request.Limit);
            await using var graphReader = await graph.ExecuteReaderAsync(cancellationToken);
            while (await graphReader.ReadAsync(cancellationToken))
            {
                var edge = JsonSerializer.Deserialize<MemoryEdge>(graphReader.GetString(0), JsonOptions)!;
                results.Add(new MemoryCandidate(edge.Id, MemoryLayer.Semantic,
                    $"{graphReader.GetString(1)} {edge.Relationship} {graphReader.GetString(2)}", edge.Confidence,
                    edge.Trust, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
                    edge.ValidFrom, edge.ValidTo, [edge.EpisodeId], "graph"));
            }
        }

        if (request.Embedding is { Count: > 0 } && Included(request, MemoryLayer.Episodic))
        {
            var vectorCandidates = new List<MemoryCandidate>();
            await using var vector = connection.CreateCommand();
            vector.CommandText = "SELECT embedding.payload,episode.payload FROM memory_embeddings embedding JOIN memory_episodes episode ON episode.id=embedding.memory_id WHERE embedding.partition_key=$partition";
            vector.Parameters.AddWithValue("$partition", request.Partition.Key);
            await using var vectorReader = await vector.ExecuteReaderAsync(cancellationToken);
            while (await vectorReader.ReadAsync(cancellationToken))
            {
                var embedding = JsonSerializer.Deserialize<MemoryEmbedding>(vectorReader.GetString(0), JsonOptions)!;
                if (embedding.Vector.Count != request.Embedding.Count) continue;
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(vectorReader.GetString(1), JsonOptions)!;
                vectorCandidates.Add(new MemoryCandidate(episode.Id, MemoryLayer.Episodic, episode.Content,
                    CosineSimilarity(request.Embedding, embedding.Vector), SourceTrust(episode.Source.Type),
                    MemoryConfirmationState.NotRequired, episode.Sensitivity,
                    episode.OccurredAt, episode.ExpiresAt, [episode.Id], "vector"));
            }
            results.AddRange(vectorCandidates.OrderByDescending(candidate => candidate.Score).Take(request.Limit));
        }
        if (Included(request, MemoryLayer.Procedural))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM memory_procedures WHERE partition_key=$partition AND confirmation IN (0,2) AND valid_from<=$now AND (valid_to IS NULL OR valid_to>$now) AND (name LIKE $query OR payload LIKE $query) LIMIT $limit";
            command.Parameters.AddWithValue("$partition", request.Partition.Key);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$query", $"%{request.Query}%");
            command.Parameters.AddWithValue("$limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var procedure = JsonSerializer.Deserialize<ProceduralMemory>(reader.GetString(0), JsonOptions)!;
                results.Add(new MemoryCandidate(procedure.Id, MemoryLayer.Procedural, procedure.Procedure, 1,
                    procedure.Trust, procedure.Confirmation, MemorySensitivity.Internal,
                    procedure.ValidFrom, procedure.ValidTo, [procedure.EpisodeId], "procedure"));
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
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_claims WHERE id=$id";
        command.Parameters.AddWithValue("$id", claimId.ToString("D"));
        return Deserialize<MemoryClaim>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await UpdateClaimAsync(claim with { Confirmation = confirmation }, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default) =>
        ListPayloadsAsync<MemoryClaim>("memory_claims", partition, cancellationToken);

    public async Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => new(
        "1.0",
        await ListPayloadsAsync<MemoryEpisode>("memory_episodes", partition, cancellationToken),
        await ListPayloadsAsync<MemoryEntity>("memory_entities", partition, cancellationToken),
        await ListPayloadsAsync<MemoryClaim>("memory_claims", partition, cancellationToken),
        await ListPayloadsAsync<MemoryEdge>("memory_edges", partition, cancellationToken),
        await ListPayloadsAsync<MemoryBlock>("memory_blocks", partition, cancellationToken),
        await ListPayloadsAsync<ProceduralMemory>("memory_procedures", partition, cancellationToken),
        await ListPayloadsAsync<MemoryEmbedding>("memory_embeddings", partition, cancellationToken));

    public async Task WriteKnowledgeTransferAsync(KnowledgeTransferPackage package, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_transfers(id,tenant_id,source_employee_id,target_employee_id,status,created_at,payload)
            VALUES($id,$tenant,$source,$target,$status,$created,$payload)
            ON CONFLICT(id) DO UPDATE SET status=excluded.status,payload=excluded.payload
            """;
        command.Parameters.AddWithValue("$id", package.Id.ToString("D"));
        command.Parameters.AddWithValue("$tenant", package.TenantId);
        command.Parameters.AddWithValue("$source", package.SourceEmployeeId);
        command.Parameters.AddWithValue("$target", package.TargetEmployeeId);
        command.Parameters.AddWithValue("$status", (int)package.Status);
        command.Parameters.AddWithValue("$created", package.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(package, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<KnowledgeTransferPackage?> GetKnowledgeTransferAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_transfers WHERE id=$id";
        command.Parameters.AddWithValue("$id", packageId.ToString("D"));
        return Deserialize<KnowledgeTransferPackage>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in new[] { "memory_uses", "memory_embeddings", "memory_procedures", "memory_blocks", "memory_edges", "memory_claims", "memory_entities", "memory_episodes" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE partition_key=$partition";
            command.Parameters.AddWithValue("$partition", partition.Key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var fts = connection.CreateCommand();
        fts.Transaction = (SqliteTransaction)transaction;
        fts.CommandText = "DELETE FROM memory_episodes_fts WHERE id NOT IN (SELECT id FROM memory_episodes)";
        await fts.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() { _initializeLock.Dispose(); return ValueTask.CompletedTask; }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken, bool initialize = true)
    {
        if (initialize) await InitializeAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<MemoryWriteResult> InsertPayloadAsync(string table, Guid id, MemoryPartition partition, string payload, CancellationToken cancellationToken, params (string Name, string? Value)[] values)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var names = string.Join(',', values.Select(value => value.Name));
        var parameters = string.Join(',', values.Select(value => '$' + value.Name));
        command.CommandText = $"INSERT INTO {table}(id,partition_key,{names},payload) VALUES($id,$partition,{parameters},$payload)";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$partition", partition.Key);
        command.Parameters.AddWithValue("$payload", payload);
        foreach (var value in values) command.Parameters.AddWithValue('$' + value.Name, Db(value.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new MemoryWriteResult(id, true);
    }

    private async Task UpdateClaimAsync(MemoryClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_claims SET confirmation=$confirmation,valid_to=$validTo,payload=$payload WHERE id=$id";
        command.Parameters.AddWithValue("$confirmation", (int)claim.Confirmation);
        command.Parameters.AddWithValue("$validTo", Db(claim.ValidTo?.ToString("O")));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(claim, JsonOptions));
        command.Parameters.AddWithValue("$id", claim.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListPayloadsAsync<T>(string table, MemoryPartition partition, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} WHERE partition_key=$partition";
        command.Parameters.AddWithValue("$partition", partition.Key);
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions)!);
        return results;
    }

    private static bool Included(MemorySearchRequest request, MemoryLayer layer) => request.Layers is null || request.Layers.Contains(layer);
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static T? Deserialize<T>(object? value) => value is string json ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;
    private static string ToFtsQuery(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0)
            .Take(20)
            .Select(word => $"\"{word}\"");
        return string.Join(" OR ", words) is { Length: > 0 } value ? value : "\"__no_match__\"";
    }
    private static MemoryTrustTier SourceTrust(string sourceType) => sourceType.Equals("application", StringComparison.OrdinalIgnoreCase)
        ? MemoryTrustTier.Authoritative : sourceType.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? MemoryTrustTier.UnconfirmedUser : MemoryTrustTier.External;
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

    private static void BindPartition(SqliteCommand command, MemoryPartition partition)
    {
        command.Parameters.AddWithValue("$partition", partition.Key);
        command.Parameters.AddWithValue("$tenant", partition.TenantId);
        command.Parameters.AddWithValue("$application", Db(partition.ApplicationId));
        command.Parameters.AddWithValue("$agent", Db(partition.AgentId));
        command.Parameters.AddWithValue("$user", Db(partition.UserId));
        command.Parameters.AddWithValue("$conversation", Db(partition.ConversationId));
        command.Parameters.AddWithValue("$custom", Db(partition.CustomNamespace));
    }

    private const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS memory_episodes(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,tenant_id TEXT NOT NULL,application_id TEXT,agent_id TEXT,user_id TEXT,conversation_id TEXT,custom_namespace TEXT,scope INTEGER NOT NULL,content TEXT NOT NULL,idempotency_key TEXT,occurred_at TEXT NOT NULL,expires_at TEXT,legal_hold INTEGER NOT NULL,payload TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_episode_idempotency ON memory_episodes(partition_key,idempotency_key) WHERE idempotency_key IS NOT NULL;
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_episodes_fts USING fts5(id UNINDEXED,content);
        CREATE TABLE IF NOT EXISTS memory_entities(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,canonical_name TEXT NOT NULL COLLATE NOCASE,application_key TEXT,payload TEXT NOT NULL,UNIQUE(partition_key,canonical_name));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_entity_application_key ON memory_entities(partition_key,application_key) WHERE application_key IS NOT NULL;
        CREATE TABLE IF NOT EXISTS memory_claims(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,episode_id TEXT NOT NULL,subject_id TEXT NOT NULL,predicate TEXT NOT NULL,value TEXT,confirmation INTEGER NOT NULL,valid_from TEXT NOT NULL,valid_to TEXT,payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_memory_claim_lookup ON memory_claims(partition_key,subject_id,predicate,valid_to);
        CREATE TABLE IF NOT EXISTS memory_edges(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,episode_id TEXT NOT NULL,from_id TEXT NOT NULL,relationship TEXT NOT NULL,to_id TEXT NOT NULL,valid_from TEXT NOT NULL,valid_to TEXT,payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_memory_edge_traversal ON memory_edges(partition_key,from_id,relationship,valid_to);
        CREATE TABLE IF NOT EXISTS memory_blocks(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,name TEXT NOT NULL,pinned INTEGER NOT NULL,payload TEXT NOT NULL,UNIQUE(partition_key,name));
        CREATE TABLE IF NOT EXISTS memory_procedures(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,episode_id TEXT NOT NULL,name TEXT NOT NULL,confirmation INTEGER NOT NULL,valid_from TEXT NOT NULL,valid_to TEXT,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS memory_embeddings(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,memory_id TEXT NOT NULL,layer INTEGER NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS memory_uses(id TEXT PRIMARY KEY,partition_key TEXT NOT NULL,memory_id TEXT NOT NULL,outcome INTEGER NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS memory_transfers(id TEXT PRIMARY KEY,tenant_id TEXT NOT NULL,source_employee_id TEXT NOT NULL,target_employee_id TEXT NOT NULL,status INTEGER NOT NULL,created_at TEXT NOT NULL,payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_memory_transfer_employees ON memory_transfers(tenant_id,source_employee_id,target_employee_id,status);
        """;
}
