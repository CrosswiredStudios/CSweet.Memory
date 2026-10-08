using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore : IMemoryStore, IKnowledgeTransferStore, IMemorySourceReader, IMemoryPartitionMigration, IMemoryRevisionReader, IMemoryTransferEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource? _dataSource;
    private readonly NpgsqlTransaction? _transaction;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private bool _schemaInitialized;

    public PostgreSqlMemoryStore(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    /// <summary>
    /// Enlists all operations in a caller-owned transaction. Initialize the schema with a
    /// regular store before opening the transaction. Disposing this store does not commit,
    /// roll back, or dispose the caller's transaction or connection.
    /// </summary>
    public PostgreSqlMemoryStore(NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Connection is null) throw new ArgumentException("The transaction must be active.", nameof(transaction));
        _transaction = transaction;
        _initialized = true;
    }

    private NpgsqlCommand CreateCommand(string sql) => _transaction is null
        ? _dataSource!.CreateCommand(sql)
        : new NpgsqlCommand(sql, _transaction.Connection ?? throw new InvalidOperationException("The transaction is no longer active."), _transaction);

    public MemoryStoreCapabilities Capabilities => MemoryStoreCapabilities.Transactions |
        MemoryStoreCapabilities.FullText | MemoryStoreCapabilities.RecursiveTraversal |
        MemoryStoreCapabilities.TemporalQueries | MemoryStoreCapabilities.BulkOperations |
        MemoryStoreCapabilities.ChangeHistory;

    private async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaInitialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaInitialized) return;
            // Schema upgrades are serialized across processes and committed atomically.
            // Once installed, avoid recurring DDL locks on normal store construction.
            await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(728145092); CREATE TABLE IF NOT EXISTS csweet_memory_schema_migrations(id text PRIMARY KEY);", connection, transaction);
            await guard.ExecuteNonQueryAsync(cancellationToken);
            await using var version = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM csweet_memory_schema_migrations WHERE id='indexed-search-v1')", connection, transaction);
            if (await version.ExecuteScalarAsync(cancellationToken) is not true)
            {
                await using var command = new NpgsqlCommand(Schema + "INSERT INTO csweet_memory_schema_migrations(id) VALUES('indexed-search-v1');", connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            _schemaInitialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        episode = MemorySourceIntegrity.Seal(episode with { IsSuppressed = false });
        await InitializeAsync(cancellationToken);
        const string sql = """
            INSERT INTO csweet_memory_episodes(id,partition_key,idempotency_key,content,occurred_at,expires_at,payload)
            VALUES(@id,@partition,@key,@content,@occurred,@expires,@payload::jsonb)
            ON CONFLICT(partition_key,idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
            RETURNING id
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("id", episode.Id);
        command.Parameters.AddWithValue("partition", episode.Partition.StorageKey);
        command.Parameters.AddWithValue("key", Db(episode.IdempotencyKey));
        command.Parameters.AddWithValue("content", episode.Content);
        command.Parameters.AddWithValue("occurred", episode.OccurredAt.ToUniversalTime());
        command.Parameters.AddWithValue("expires", Db(episode.ExpiresAt));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(episode, JsonOptions));
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is Guid id) return new MemoryWriteResult(id, true);
        await using var existing = CreateCommand("SELECT id FROM csweet_memory_episodes WHERE partition_key=@partition AND idempotency_key=@key");
        existing.Parameters.AddWithValue("partition", episode.Partition.StorageKey);
        existing.Parameters.AddWithValue("key", episode.IdempotencyKey!);
        return new MemoryWriteResult((Guid)(await existing.ExecuteScalarAsync(cancellationToken))!, false, "Idempotent replay.");
    }

    public async Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(entity.SourceEpisodeIds);
        await InitializeAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(entity.ApplicationKey))
        {
            var identified = await FindRawEntityByApplicationKeyAsync(entity.Partition, entity.ApplicationKey, cancellationToken);
            if (identified is not null)
            {
                entity = entity with { Id = identified.Id, CreatedAt = identified.CreatedAt };
                var merged = MergeSourceIds("payload", "(@payload::jsonb)");
                await using var update = CreateCommand($"UPDATE csweet_memory_entities SET canonical_name=@name,payload=jsonb_set(jsonb_set(@payload::jsonb,'{{sensitivity}}',to_jsonb(GREATEST(COALESCE((payload->>'sensitivity')::int,4),COALESCE((@payload::jsonb->>'sensitivity')::int,4)))),'{{sourceEpisodeIds}}',{merged}) WHERE id=@id AND jsonb_array_length({merged})<=128");
                update.Parameters.AddWithValue("name", entity.CanonicalName);
                update.Parameters.AddWithValue("payload", JsonSerializer.Serialize(entity, JsonOptions));
                update.Parameters.AddWithValue("id", entity.Id);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("memory_lineage_limit");
                return new MemoryWriteResult(entity.Id, false, "Updated by authoritative application key.");
            }
        }
        var union = MergeSourceIds("csweet_memory_entities.payload", "excluded.payload");
        var sql = $$"""
            INSERT INTO csweet_memory_entities(id,partition_key,canonical_name,application_key,payload)
            VALUES(@id,@partition,@name,@applicationKey,@payload::jsonb)
            ON CONFLICT(partition_key,lower(canonical_name)) DO UPDATE SET application_key=excluded.application_key,
                payload=jsonb_set(jsonb_set(jsonb_set(excluded.payload,'{id}',to_jsonb(csweet_memory_entities.id::text)),
                    '{sensitivity}',to_jsonb(GREATEST(COALESCE((csweet_memory_entities.payload->>'sensitivity')::int,4),COALESCE((excluded.payload->>'sensitivity')::int,4)))),
                    '{sourceEpisodeIds}',{{union}}) WHERE jsonb_array_length({{union}})<=128
            RETURNING id
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("id", entity.Id);
        command.Parameters.AddWithValue("partition", entity.Partition.StorageKey);
        command.Parameters.AddWithValue("name", entity.CanonicalName);
        command.Parameters.AddWithValue("applicationKey", Db(entity.ApplicationKey));
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(entity, JsonOptions));
        var id = await command.ExecuteScalarAsync(cancellationToken) is Guid storedId ? storedId
            : throw new InvalidOperationException("memory_lineage_limit");
        return new MemoryWriteResult(id, id == entity.Id);
    }

    public async Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition AND lower(canonical_name)=lower(@name) LIMIT 1");
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("name", canonicalName);
        var exact = Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
        if (exact is not null) return await ResolveEntityAsync(exact, partition, cancellationToken);
        MemoryEntity? alias = null;
        await foreach (var json in QueryPayloadsAsync("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition", partition.StorageKey, cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<MemoryEntity>(json, JsonOptions)!;
            if (entity.Aliases.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
            { alias = entity; break; }
        }
        return await ResolveEntityAsync(alias, partition, cancellationToken);
    }

    public async Task<MemoryEntity?> FindEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken = default) =>
        await ResolveEntityAsync(await FindRawEntityByApplicationKeyAsync(partition, applicationKey, cancellationToken), partition, cancellationToken);

    private async Task<MemoryEntity?> FindRawEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_entities WHERE partition_key=@partition AND application_key=@key LIMIT 1");
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("key", applicationKey);
        return Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<MemoryEpisode?> GetEpisodeAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default)
    {
        var episode = await ReadSourceAsync<MemoryEpisode>("csweet_memory_episodes", partition, id, cancellationToken);
        return episode?.Partition == partition && episode.Id == id
            ? (await ResolveTransferEpisodesAsync([episode], DateTimeOffset.UtcNow, cancellationToken))[0] : null;
    }

    public async Task<MemoryEntity?> GetEntityAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await ReadSourceAsync<MemoryEntity>("csweet_memory_entities", partition, id, cancellationToken);
        return entity?.Partition == partition && entity.Id == id ? await ResolveEntityAsync(entity, partition, cancellationToken) : null;
    }

    private async Task<T?> ReadSourceAsync<T>(string table, MemoryPartition partition, Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand($"SELECT payload::text FROM {table} WHERE partition_key=@partition AND id=@id");
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("id", id);
        return Deserialize<T>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(claim.SourceEpisodeIds);
        return InsertAsync("csweet_memory_claims", claim.Id, claim.Partition.StorageKey, JsonSerializer.Serialize(claim, JsonOptions), cancellationToken,
            ("episode_id", claim.EpisodeId), ("subject_id", claim.SubjectEntityId), ("predicate", claim.Predicate),
            ("value", claim.Value), ("confirmation", (int)claim.Confirmation), ("valid_from", claim.ValidFrom), ("valid_to", claim.ValidTo));
    }

    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(edge.SourceEpisodeIds);
        return InsertAsync("csweet_memory_edges", edge.Id, edge.Partition.StorageKey, JsonSerializer.Serialize(edge, JsonOptions), cancellationToken,
            ("episode_id", edge.EpisodeId), ("from_id", edge.FromEntityId), ("relationship", edge.Relationship),
            ("to_id", edge.ToEntityId), ("valid_from", edge.ValidFrom), ("valid_to", edge.ValidTo));
    }

    public async Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(block.SourceEpisodeIds);
        await InitializeAsync(cancellationToken);
        var union = MergeSourceIds("csweet_memory_blocks.payload", "excluded.payload");
        var sql = $$"""
            INSERT INTO csweet_memory_blocks(id,partition_key,name,pinned,payload) VALUES(@id,@partition,@name,@pinned,@payload::jsonb)
            ON CONFLICT(partition_key,name) DO UPDATE SET pinned=excluded.pinned,
                payload=jsonb_set(jsonb_set(jsonb_set(excluded.payload,'{id}',to_jsonb(csweet_memory_blocks.id::text)),'{sourceEpisodeIds}',{{union}}),'{sensitivity}',
                    to_jsonb(GREATEST(COALESCE((csweet_memory_blocks.payload->>'sensitivity')::int,4),COALESCE((excluded.payload->>'sensitivity')::int,4))))
            WHERE jsonb_array_length({{union}})<=128 RETURNING id
            """;
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("id", block.Id);
        command.Parameters.AddWithValue("partition", block.Partition.StorageKey);
        command.Parameters.AddWithValue("name", block.Name);
        command.Parameters.AddWithValue("pinned", block.IsPinned);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(block, JsonOptions));
        var id = await command.ExecuteScalarAsync(cancellationToken) is Guid storedId ? storedId
            : throw new InvalidOperationException("memory_lineage_limit");
        return new MemoryWriteResult(id, id == block.Id);
    }

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(procedure.SourceEpisodeIds);
        return InsertAsync("csweet_memory_procedures", procedure.Id, procedure.Partition.StorageKey, JsonSerializer.Serialize(procedure, JsonOptions), cancellationToken,
            ("episode_id", procedure.EpisodeId), ("name", procedure.Name), ("confirmation", (int)procedure.Confirmation),
            ("valid_from", procedure.ValidFrom), ("valid_to", procedure.ValidTo));
    }

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        InsertAsync("csweet_memory_embeddings", embedding.Id, embedding.Partition.StorageKey, JsonSerializer.Serialize(embedding, JsonOptions), cancellationToken,
            ("memory_id", embedding.MemoryId), ("layer", (int)embedding.Layer));

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand("INSERT INTO csweet_memory_uses(id,partition_key,memory_id,outcome,payload) VALUES(@id,@partition,@memory,@outcome,@payload::jsonb)");
        command.Parameters.AddWithValue("id", use.Id);
        command.Parameters.AddWithValue("partition", use.Partition.StorageKey);
        command.Parameters.AddWithValue("memory", use.MemoryId);
        command.Parameters.AddWithValue("outcome", (int)use.Outcome);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(use, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var results = new List<MemoryCandidate>();
        var lexical = MemoryLexicalQuery.Parse(request.Query);
        if (lexical.Terms.Count == 0) return results;
        request = request with { Limit = Math.Clamp(request.Limit, 1, 100) };
        var asOf = (request.AsOf ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (Included(request, MemoryLayer.Core))
        {
            await foreach (var json in QueryPayloadsAsync("SELECT payload::text FROM csweet_memory_blocks WHERE partition_key=@partition ORDER BY pinned DESC,id LIMIT 100", request.Partition.StorageKey, cancellationToken))
            {
                var block = JsonSerializer.Deserialize<MemoryBlock>(json, JsonOptions)!;
                if (block.Partition != request.Partition || block.UpdatedAt > asOf ||
                    (block.Confirmation is not (MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed) &&
                     !(request.IncludePending && block.Confirmation == MemoryConfirmationState.Pending)) ||
                    block.SourceEpisodeIds is null || block.SourceEpisodeIds.Count > MemoryProvenance.MaximumSourceEpisodes) continue;
                results.Add(new(block.Id, MemoryLayer.Core, block.Content, block.IsPinned ? 2 : 1, block.Trust,
                    block.Confirmation, block.Sensitivity, block.UpdatedAt, null, block.SourceEpisodeIds, "core"));
            }
        }
        if (Included(request, MemoryLayer.Episodic))
        {
            var sql = $"""
                SELECT payload::text,ts_rank_cd(search_vector,websearch_to_tsquery('simple',@query)) score FROM csweet_memory_episodes
                WHERE partition_key=@partition AND search_vector @@ websearch_to_tsquery('simple',@query)
                    AND COALESCE(payload->>'isSuppressed','false')='false' AND {ValidAt("payload", "occurredAt", "expiresAt")}
                ORDER BY score DESC,id LIMIT @limit
                """;
            await using var command = CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("query", lexical.FullText);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), JsonOptions)!;
                if (!MemoryProvenance.IsSnapshotCurrent(episode, request.Partition, episode.Id, asOf)) continue;
                results.Add(new(episode.Id, MemoryLayer.Episodic, episode.Content, reader.GetDouble(1), SourceTrust(episode.Source.Type),
                    MemoryConfirmationState.NotRequired, episode.Sensitivity, episode.OccurredAt, episode.ExpiresAt, [episode.Id], "fulltext"));
            }
        }
        if (Included(request, MemoryLayer.Semantic))
        {
            var sql = $"""
                SELECT c.payload::text,e.payload::text,p.payload::text,o.payload::text FROM csweet_memory_claims c
                JOIN csweet_memory_entities e ON e.id=c.subject_id AND e.partition_key=c.partition_key
                JOIN csweet_memory_episodes p ON p.id=c.episode_id AND p.partition_key=c.partition_key
                LEFT JOIN csweet_memory_entities o ON o.id=(c.payload->>'objectEntityId')::uuid AND o.partition_key=c.partition_key
                WHERE c.partition_key=@partition AND {ValidAt("c.payload", "validFrom", "validTo", includeSuperseded: true)}
                  AND c.confirmation IN (0,2,CASE WHEN @pending THEN 1 ELSE 2 END)
                  AND COALESCE(p.payload->>'isSuppressed','false')='false' AND {ValidAt("p.payload", "occurredAt", "expiresAt")}
                  AND (c.search_vector @@ websearch_to_tsquery('simple',@query)
                    OR c.subject_id IN (SELECT id FROM csweet_memory_entities WHERE partition_key=@partition AND search_vector @@ websearch_to_tsquery('simple',@query))
                    OR (c.payload->>'objectEntityId')::uuid IN (SELECT id FROM csweet_memory_entities WHERE partition_key=@partition AND search_vector @@ websearch_to_tsquery('simple',@query)))
                ORDER BY (lower(e.canonical_name)=@phrase OR lower(o.canonical_name)=@phrase) DESC NULLS LAST,
                    ts_rank_cd(c.search_vector,phraseto_tsquery('simple',@phrase)) DESC,
                    ts_rank_cd(c.search_vector,websearch_to_tsquery('simple',@query)) DESC,c.id LIMIT @limit
                """;
            await using var command = CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            command.Parameters.AddWithValue("superseded", request.IncludeSuperseded);
            command.Parameters.AddWithValue("pending", request.IncludePending);
            command.Parameters.AddWithValue("phrase", string.Join(' ', lexical.Terms));
            command.Parameters.AddWithValue("query", lexical.FullText);
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var claim = JsonSerializer.Deserialize<MemoryClaim>(reader.GetString(0), JsonOptions)!;
                var subject = JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(1), JsonOptions)!;
                var source = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(2), JsonOptions)!;
                var objectEntity = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(3), JsonOptions);
                var resolved = MemoryProvenance.ResolveClaimReferences(claim, source, subject, objectEntity, asOf, snapshotOnly: true);
                if (claim.Partition != request.Partition || resolved is null || !MemoryProvenance.HasBoundedSources(claim.SourceEpisodeIds) ||
                    !HasBoundedLineage(subject) || (objectEntity is not null && !HasBoundedLineage(objectEntity))) continue;
                claim = resolved;
                results.Add(new(claim.Id, MemoryLayer.Semantic, $"{subject.CanonicalName} {claim.Predicate} {claim.Value ?? objectEntity?.CanonicalName}",
                    claim.Confidence * claim.Importance, claim.Trust, claim.Confirmation, claim.Sensitivity,
                    claim.ValidFrom, claim.ValidTo, new[] { claim.EpisodeId }.Concat(claim.SourceEpisodeIds).Concat(subject.SourceEpisodeIds)
                        .Concat(objectEntity?.SourceEpisodeIds ?? []).Distinct().ToArray(), "semantic"));
            }

            await reader.DisposeAsync();
            results.AddRange(await SearchGraphAsync(request, lexical, asOf, cancellationToken));
        }

        if (request.Embedding is { Count: > 0 } && Included(request, MemoryLayer.Episodic))
        {
            var vectorSql = $"SELECT embedding.payload::text,episode.payload::text FROM csweet_memory_embeddings embedding JOIN csweet_memory_episodes episode ON episode.id=embedding.memory_id AND episode.partition_key=embedding.partition_key WHERE embedding.partition_key=@partition AND embedding.layer=1 AND COALESCE(episode.payload->>'isSuppressed','false')='false' AND {ValidAt("episode.payload", "occurredAt", "expiresAt")} ORDER BY embedding.id LIMIT 1024";
            await using var vector = CreateCommand(vectorSql);
            vector.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            vector.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            var vectorCandidates = new List<MemoryCandidate>();
            await using var vectorReader = await vector.ExecuteReaderAsync(cancellationToken);
            while (await vectorReader.ReadAsync(cancellationToken))
            {
                var embedding = JsonSerializer.Deserialize<MemoryEmbedding>(vectorReader.GetString(0), JsonOptions)!;
                if (embedding.Vector.Count != request.Embedding.Count) continue;
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(vectorReader.GetString(1), JsonOptions)!;
                if (embedding.Partition != request.Partition || embedding.Layer != MemoryLayer.Episodic ||
                    !MemoryProvenance.IsSnapshotCurrent(episode, request.Partition, embedding.MemoryId, asOf)) continue;
                vectorCandidates.Add(new(episode.Id, MemoryLayer.Episodic, episode.Content, CosineSimilarity(request.Embedding, embedding.Vector),
                    SourceTrust(episode.Source.Type), MemoryConfirmationState.NotRequired, episode.Sensitivity,
                    episode.OccurredAt, episode.ExpiresAt, [episode.Id], "vector"));
            }
            results.AddRange(vectorCandidates.OrderByDescending(candidate => candidate.Score).Take(request.Limit));
        }
        if (Included(request, MemoryLayer.Procedural))
        {
            var sql = $"""
                SELECT procedure.payload::text,source.payload::text FROM csweet_memory_procedures procedure
                JOIN csweet_memory_episodes source ON source.id=procedure.episode_id AND source.partition_key=procedure.partition_key
                WHERE procedure.partition_key=@partition AND procedure.confirmation IN (0,2)
                    AND {ValidAt("procedure.payload", "validFrom", "validTo")}
                    AND COALESCE(source.payload->>'isSuppressed','false')='false' AND {ValidAt("source.payload", "occurredAt", "expiresAt")}
                    AND procedure.search_vector @@ websearch_to_tsquery('simple',@query)
                ORDER BY (lower(procedure.name)=@phrase) DESC,
                    ts_rank_cd(procedure.search_vector,phraseto_tsquery('simple',@phrase)) DESC,
                    ts_rank_cd(procedure.search_vector,websearch_to_tsquery('simple',@query)) DESC,procedure.id LIMIT @limit
                """;
            await using var command = CreateCommand(sql);
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            command.Parameters.AddWithValue("query", lexical.FullText);
            command.Parameters.AddWithValue("phrase", string.Join(' ', lexical.Terms));
            command.Parameters.AddWithValue("limit", request.Limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var procedure = JsonSerializer.Deserialize<ProceduralMemory>(reader.GetString(0), JsonOptions)!;
                var source = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(1), JsonOptions);
                if (procedure.Partition != request.Partition || !MemoryProvenance.HasBoundedSources(procedure.SourceEpisodeIds) ||
                    !MemoryProvenance.IsSnapshotCurrent(source, request.Partition, procedure.EpisodeId, asOf)) continue;
                results.Add(new(procedure.Id, MemoryLayer.Procedural, procedure.Procedure, 1, procedure.Trust, procedure.Confirmation,
                    source!.Sensitivity, procedure.ValidFrom, procedure.ValidTo, new[] { procedure.EpisodeId }.Concat(procedure.SourceEpisodeIds).Distinct().ToArray(), "procedure"));
            }
        }
        return await ResolveCandidatesAsync(results, request.Partition, asOf, cancellationToken);
    }

    public async Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default)
    {
        var claim = await GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException();
        await UpdateClaimAsync(claim with { ValidTo = validTo }, cancellationToken);
    }

    public async Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_claims WHERE id=@id AND partition_key LIKE 'mp2:%'");
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
        "1.0", await ResolveTransferEpisodesAsync(await ListAsync<MemoryEpisode>("csweet_memory_episodes", partition, cancellationToken), DateTimeOffset.UtcNow, cancellationToken),
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
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("id", package.Id);
        command.Parameters.AddWithValue("tenant", package.TenantId);
        command.Parameters.AddWithValue("source", package.SourceEmployeeId);
        command.Parameters.AddWithValue("target", package.TargetEmployeeId);
        command.Parameters.AddWithValue("status", (int)package.Status);
        command.Parameters.AddWithValue("created", package.CreatedAt.ToUniversalTime());
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(package, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<KnowledgeTransferPackage?> GetKnowledgeTransferAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_transfers WHERE id=@id AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows WHERE table_name='csweet_memory_transfers' AND record_id=CAST(@id AS text) AND disposition='Quarantine')");
        command.Parameters.AddWithValue("id", packageId);
        return Deserialize<KnowledgeTransferPackage>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (_transaction is not null)
        {
            // Serialize the retention check with episode inserts/hold updates. A hold is a
            // retention barrier, never permission to silently delete the rest of a scope.
            await using (var barrier = CreateCommand("LOCK TABLE csweet_memory_episodes,csweet_memory_entities,csweet_memory_claims,csweet_memory_edges,csweet_memory_blocks,csweet_memory_procedures IN SHARE ROW EXCLUSIVE MODE"))
                await barrier.ExecuteNonQueryAsync(cancellationToken);
            await using (var held = CreateCommand("SELECT EXISTS(SELECT 1 FROM csweet_memory_episodes WHERE partition_key=@partition AND COALESCE((payload->>'legalHold')::boolean,false))"))
            {
                held.Parameters.AddWithValue("partition", partition.StorageKey);
                if ((bool)(await held.ExecuteScalarAsync(cancellationToken))!)
                    throw new InvalidOperationException("memory_legal_hold_prevents_deletion");
            }
            await new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, DateTimeOffset.UtcNow).EnsureDeletionAllowedAsync(partition, cancellationToken);
            foreach (var table in Tables.AsEnumerable().Reverse().Append("csweet_memory_revisions"))
            {
                await using var command = CreateCommand($"DELETE FROM {table} WHERE partition_key=@partition");
                command.Parameters.AddWithValue("partition", partition.StorageKey);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            return;
        }
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var enlisted = new PostgreSqlMemoryStore(transaction);
        await enlisted.DeleteScopeAsync(partition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _initializeLock.Dispose();
        if (_dataSource is not null) await _dataSource.DisposeAsync();
    }

    private async Task<MemoryWriteResult> InsertAsync(string table, Guid id, string partition, string payload, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await InitializeAsync(cancellationToken);
        var columns = string.Join(',', values.Select(value => value.Name));
        var parameters = string.Join(',', values.Select(value => '@' + value.Name));
        await using var command = CreateCommand($"INSERT INTO {table}(id,partition_key,{columns},payload) VALUES(@id,@partition,{parameters},@payload::jsonb) ON CONFLICT(id) DO NOTHING RETURNING id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("partition", partition);
        command.Parameters.AddWithValue("payload", payload);
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, Db(value.Value));
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid)
            return new MemoryWriteResult(id, true);
        // The first recording timestamp survives retries. Every other field, including
        // full partition identity and current review state, must still match.
        // Normalize only an omitted additive source list, preserving populated and null values.
        var defaults = table == "csweet_memory_embeddings" ? "'{}'::jsonb" : "'{\"sourceEpisodeIds\":[]}'::jsonb";
        await using var replay = CreateCommand($"SELECT ({defaults} || payload) - 'recordedAt' = ({defaults} || @payload::jsonb) - 'recordedAt' FROM {table} WHERE id=@id AND partition_key=@partition");
        replay.Parameters.AddWithValue("id", id);
        replay.Parameters.AddWithValue("partition", partition);
        replay.Parameters.AddWithValue("payload", payload);
        if (await replay.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("memory_write_conflict");
        return new MemoryWriteResult(id, false, "Idempotent replay.");
    }

    private async Task UpdateClaimAsync(MemoryClaim claim, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand("UPDATE csweet_memory_claims SET confirmation=@confirmation,valid_to=@validTo,payload=@payload::jsonb WHERE id=@id");
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
        await foreach (var json in QueryPayloadsAsync($"SELECT payload::text FROM {table} WHERE partition_key=@partition", partition.StorageKey, cancellationToken))
            results.Add(JsonSerializer.Deserialize<T>(json, JsonOptions)!);
        return results;
    }

    private async IAsyncEnumerable<string> QueryPayloadsAsync(string sql, string partition, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        command.Parameters.AddWithValue("partition", partition);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) yield return reader.GetString(0);
    }

    private static bool Included(MemorySearchRequest request, MemoryLayer layer) => request.Layers is null || request.Layers.Contains(layer);
    private static object Db(object? value) => value is DateTimeOffset instant ? instant.ToUniversalTime() : value ?? DBNull.Value;
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
        -- Additive indexed-search migration: generated columns populate existing rows and
        -- stay synchronized for old writers. No sensitivity or namespace data is changed.
        ALTER TABLE csweet_memory_entities ADD COLUMN IF NOT EXISTS search_vector tsvector GENERATED ALWAYS AS (to_tsvector('simple',canonical_name || ' ' || COALESCE(payload->>'aliases',''))) STORED;
        ALTER TABLE csweet_memory_claims ADD COLUMN IF NOT EXISTS search_vector tsvector GENERATED ALWAYS AS (to_tsvector('simple',predicate || ' ' || COALESCE(value,''))) STORED;
        ALTER TABLE csweet_memory_procedures ADD COLUMN IF NOT EXISTS search_vector tsvector GENERATED ALWAYS AS (to_tsvector('simple',name || ' ' || COALESCE(payload->>'procedure','') || ' ' || COALESCE(payload->>'applicability',''))) STORED;
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_entity_search ON csweet_memory_entities USING GIN(search_vector);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_claim_search ON csweet_memory_claims USING GIN(search_vector);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_procedure_search ON csweet_memory_procedures USING GIN(search_vector);
        CREATE INDEX IF NOT EXISTS ix_csweet_memory_claim_object ON csweet_memory_claims(partition_key,((payload->>'objectEntityId')::uuid));
        """;
}
