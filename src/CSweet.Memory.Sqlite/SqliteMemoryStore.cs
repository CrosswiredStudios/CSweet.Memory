using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore : IMemoryStore, IKnowledgeTransferStore, IMemorySourceReader, IMemoryPartitionMigration, IMemoryRevisionReader, IMemoryTransferEvidenceStore, IMemoryCorrectionEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private bool _schemaInitialized;

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

    private async Task InitializeSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaInitialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaInitialized) return;
            await using var connection = await OpenAsync(cancellationToken, initialize: false);
            await using var command = connection.CreateCommand();
            command.CommandText = Schema + IndexedSearchMigration;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await UpgradeEntityAliasIndexAsync(connection, cancellationToken);
            await UpgradeCoreSearchIndexAsync(connection, cancellationToken);
            _schemaInitialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    private static async Task UpgradeEntityAliasIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Serialize the marker check, trigger replacement and populated backfill with writers.
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM memory_schema_migrations WHERE id='decoded-entity-aliases-v2'";
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            const string content = "canonical_name || ' ' || coalesce((SELECT group_concat(value,' ') FROM json_each(payload,'$.aliases') WHERE type='text'),'')";
            var inserted = content.Replace("canonical_name", "new.canonical_name").Replace("payload", "new.payload");
            command.CommandText = $"""
                DROP TRIGGER IF EXISTS memory_entities_fts_insert;
                DROP TRIGGER IF EXISTS memory_entities_fts_update;
                CREATE TRIGGER memory_entities_fts_insert AFTER INSERT ON memory_entities BEGIN
                    INSERT INTO memory_entities_fts(id,content) VALUES(new.id,{inserted});
                END;
                CREATE TRIGGER memory_entities_fts_update AFTER UPDATE ON memory_entities BEGIN
                    DELETE FROM memory_entities_fts WHERE id=old.id;
                    INSERT INTO memory_entities_fts(id,content) VALUES(new.id,{inserted});
                END;
                DELETE FROM memory_entities_fts;
                INSERT INTO memory_entities_fts(id,content) SELECT id,{content} FROM memory_entities;
                INSERT INTO memory_schema_migrations(id) VALUES('decoded-entity-aliases-v2');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        episode = MemorySourceIntegrity.Seal(episode with { IsSuppressed = false });
        await using var connection = await OpenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(episode.IdempotencyKey))
        {
            await using var existing = connection.CreateCommand();
            existing.CommandText = "SELECT id FROM memory_episodes WHERE partition_key=$partition AND idempotency_key=$key LIMIT 1";
            existing.Parameters.AddWithValue("$partition", episode.Partition.StorageKey);
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
        command.Parameters.AddWithValue("$occurred", episode.OccurredAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expires", Db(episode.ExpiresAt?.ToUniversalTime().ToString("O")));
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
        MemoryProvenance.ValidateSourceEpisodes(entity.SourceEpisodeIds);
        if (!string.IsNullOrWhiteSpace(entity.ApplicationKey))
        {
            var identified = await FindRawEntityByApplicationKeyAsync(entity.Partition, entity.ApplicationKey, cancellationToken);
            if (identified is not null)
            {
                entity = entity with { Id = identified.Id, CreatedAt = identified.CreatedAt };
                await using var updateConnection = await OpenAsync(cancellationToken);
                await using var update = updateConnection.CreateCommand();
                var merged = MergeSourceIds("payload", "$payload");
                update.CommandText = $"UPDATE memory_entities SET canonical_name=$name,payload=json_set($payload,'$.sourceEpisodeIds',json({merged}),'$.sensitivity',max(coalesce(json_extract(payload,'$.sensitivity'),4),coalesce(json_extract($payload,'$.sensitivity'),4))) WHERE id=$id AND json_array_length({merged})<=128";
                update.Parameters.AddWithValue("$name", entity.CanonicalName);
                update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entity, JsonOptions));
                update.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("memory_lineage_limit");
                return new MemoryWriteResult(entity.Id, false, "Updated by authoritative application key.");
            }
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var union = MergeSourceIds("memory_entities.payload", "excluded.payload");
        command.CommandText = $$"""
            INSERT INTO memory_entities(id, partition_key, canonical_name, application_key, payload)
            VALUES($id,$partition,$name,$applicationKey,$payload)
            ON CONFLICT(partition_key, canonical_name) DO UPDATE SET
                payload=json_set(excluded.payload,'$.id',memory_entities.id,'$.sourceEpisodeIds',json({{union}}),'$.sensitivity',
                    max(coalesce(json_extract(memory_entities.payload,'$.sensitivity'),4),coalesce(json_extract(excluded.payload,'$.sensitivity'),4))),
                application_key=excluded.application_key WHERE json_array_length({{union}})<=128 RETURNING id
            """;
        command.Parameters.AddWithValue("$id", entity.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", entity.Partition.StorageKey);
        command.Parameters.AddWithValue("$name", entity.CanonicalName);
        command.Parameters.AddWithValue("$applicationKey", Db(entity.ApplicationKey));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entity, JsonOptions));
        var stored = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("memory_lineage_limit");
        var storedId = Guid.Parse(stored);
        return new MemoryWriteResult(storedId, storedId == entity.Id);
    }

    public async Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition AND canonical_name=$name COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
        command.Parameters.AddWithValue("$name", canonicalName);
        var exact = Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
        if (exact is not null) return await ResolveEntityAsync(exact, partition, cancellationToken);
        await using var aliases = connection.CreateCommand();
        aliases.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition";
        aliases.Parameters.AddWithValue("$partition", partition.StorageKey);
        await using var reader = await aliases.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entity = JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(0), JsonOptions)!;
            if (entity.Aliases.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
                return await ResolveEntityAsync(entity, partition, cancellationToken);
        }
        return null;
    }

    public async Task<MemoryEntity?> FindEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken = default) =>
        await ResolveEntityAsync(await FindRawEntityByApplicationKeyAsync(partition, applicationKey, cancellationToken), partition, cancellationToken);

    private async Task<MemoryEntity?> FindRawEntityByApplicationKeyAsync(MemoryPartition partition, string applicationKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM memory_entities WHERE partition_key=$partition AND application_key=$key LIMIT 1";
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
        command.Parameters.AddWithValue("$key", applicationKey);
        return Deserialize<MemoryEntity>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(claim.SourceEpisodeIds);
        return InsertPayloadAsync("memory_claims", claim.Id, claim.Partition, JsonSerializer.Serialize(claim, JsonOptions), cancellationToken,
            ("episode_id", claim.EpisodeId.ToString("D")), ("subject_id", claim.SubjectEntityId.ToString("D")),
            ("predicate", claim.Predicate), ("value", claim.Value), ("confirmation", ((int)claim.Confirmation).ToString(CultureInfo.InvariantCulture)),
            ("valid_from", claim.ValidFrom.ToUniversalTime().ToString("O")), ("valid_to", claim.ValidTo?.ToUniversalTime().ToString("O")));
    }

    public async Task<MemoryEpisode?> GetEpisodeAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default)
    {
        var episode = await ReadSourceAsync<MemoryEpisode>("memory_episodes", partition, id, cancellationToken);
        return episode?.Partition == partition && episode.Id == id
            ? (await ResolveTransferEpisodesAsync([episode], DateTimeOffset.UtcNow, cancellationToken))[0] : null;
    }

    public async Task<MemoryEntity?> GetEntityAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await ReadSourceAsync<MemoryEntity>("memory_entities", partition, id, cancellationToken);
        return entity?.Partition == partition && entity.Id == id ? await ResolveEntityAsync(entity, partition, cancellationToken) : null;
    }

    private async Task<T?> ReadSourceAsync<T>(string table, MemoryPartition partition, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} WHERE partition_key=$partition AND id=$id";
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Deserialize<T>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(edge.SourceEpisodeIds);
        return InsertPayloadAsync("memory_edges", edge.Id, edge.Partition, JsonSerializer.Serialize(edge, JsonOptions), cancellationToken,
            ("episode_id", edge.EpisodeId.ToString("D")), ("from_id", edge.FromEntityId.ToString("D")),
            ("relationship", edge.Relationship), ("to_id", edge.ToEntityId.ToString("D")),
            ("valid_from", edge.ValidFrom.ToUniversalTime().ToString("O")), ("valid_to", edge.ValidTo?.ToUniversalTime().ToString("O")));
    }

    public async Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(block.SourceEpisodeIds);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var union = MergeSourceIds("memory_blocks.payload", "excluded.payload");
        command.CommandText = $$"""
            INSERT INTO memory_blocks(id, partition_key, name, pinned, payload) VALUES($id,$partition,$name,$pinned,$payload)
            ON CONFLICT(partition_key,name) DO UPDATE SET pinned=excluded.pinned,
                payload=json_set(excluded.payload,'$.id',memory_blocks.id,'$.sourceEpisodeIds',json({{union}}),'$.sensitivity',
                    max(coalesce(json_extract(memory_blocks.payload,'$.sensitivity'),4),coalesce(json_extract(excluded.payload,'$.sensitivity'),4)))
            WHERE json_array_length({{union}})<=128 RETURNING id
            """;
        command.Parameters.AddWithValue("$id", block.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", block.Partition.StorageKey);
        command.Parameters.AddWithValue("$name", block.Name);
        command.Parameters.AddWithValue("$pinned", block.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(block, JsonOptions));
        var id = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("memory_lineage_limit");
        return new MemoryWriteResult(Guid.Parse(id), true);
    }

    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default)
    {
        MemoryProvenance.ValidateSourceEpisodes(procedure.SourceEpisodeIds);
        return InsertPayloadAsync("memory_procedures", procedure.Id, procedure.Partition, JsonSerializer.Serialize(procedure, JsonOptions), cancellationToken,
            ("episode_id", procedure.EpisodeId.ToString("D")), ("name", procedure.Name),
            ("confirmation", ((int)procedure.Confirmation).ToString(CultureInfo.InvariantCulture)),
            ("valid_from", procedure.ValidFrom.ToUniversalTime().ToString("O")), ("valid_to", procedure.ValidTo?.ToUniversalTime().ToString("O")));
    }

    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) =>
        InsertPayloadAsync("memory_embeddings", embedding.Id, embedding.Partition, JsonSerializer.Serialize(embedding, JsonOptions), cancellationToken,
            ("memory_id", embedding.MemoryId.ToString("D")), ("layer", ((int)embedding.Layer).ToString(CultureInfo.InvariantCulture)));

    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO memory_uses(id,partition_key,memory_id,outcome,payload) VALUES($id,$partition,$memory,$outcome,$payload)";
        command.Parameters.AddWithValue("$id", use.Id.ToString("D"));
        command.Parameters.AddWithValue("$partition", use.Partition.StorageKey);
        command.Parameters.AddWithValue("$memory", use.MemoryId.ToString("D"));
        command.Parameters.AddWithValue("$outcome", (int)use.Outcome);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(use, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MemorySearchPage<MemoryCandidate>> SearchPageAsync(MemorySearchRequest request, int offset, MemorySearchBudget budget, CancellationToken cancellationToken)
    {
        var results = new List<MemoryCandidate>();
        var rows = 0;
        var lexical = MemoryLexicalQuery.Parse(request.Query);
        if (lexical.Terms.Count == 0) return new(results, rows);
        request = request with { Limit = Math.Clamp(request.Limit, 1, 100) };
        await using var connection = await OpenAsync(cancellationToken);
        var asOf = request.AsOf ?? DateTimeOffset.UtcNow;
        var now = asOf.UtcTicks;
        if (Included(request, MemoryLayer.Core))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT b.payload FROM memory_blocks b
                WHERE b.partition_key=$partition
                  AND (($pinned=1 AND b.pinned=1) OR b.id IN (SELECT id FROM memory_blocks_fts WHERE content MATCH $query))
                  AND csweet_utc_ticks(json_extract(b.payload,'$.updatedAt'))<=$now
                  AND coalesce(json_extract(b.payload,'$.confirmation'),0) IN (0,2,CASE WHEN $pending=1 THEN 1 ELSE 2 END)
                  AND {SourceHeadersEligible("b.payload", "b.partition_key")}
                ORDER BY b.pinned DESC,b.id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$pinned", request.IncludePinnedCore ? 1 : 0);
            command.Parameters.AddWithValue("$query", lexical.FullText);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$pending", request.IncludePending ? 1 : 0);
            command.Parameters.AddWithValue("$limit", request.Limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Candidate();
                rows++;
                var block = JsonSerializer.Deserialize<MemoryBlock>(budget.Payload(reader.GetString(0)), JsonOptions)!;
                if (block.Partition != request.Partition || block.UpdatedAt > asOf ||
                    (block.Confirmation is not (MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed) &&
                     !(request.IncludePending && block.Confirmation == MemoryConfirmationState.Pending)) ||
                    block.SourceEpisodeIds is null || block.SourceEpisodeIds.Count > MemoryProvenance.MaximumSourceEpisodes) continue;
                results.Add(new MemoryCandidate(block.Id, MemoryLayer.Core, block.Content, block.IsPinned ? 2 : 1,
                    block.Trust, block.Confirmation, block.Sensitivity, block.UpdatedAt, null, block.SourceEpisodeIds, "core"));
            }
        }
        if (Included(request, MemoryLayer.Episodic))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.payload FROM memory_episodes e JOIN memory_episodes_fts f ON f.id=e.id
                WHERE e.partition_key=$partition AND f.content MATCH $query AND COALESCE(json_extract(e.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(e.occurred_at)<=$now AND (e.expires_at IS NULL OR csweet_utc_ticks(e.expires_at)>$now)
                ORDER BY bm25(memory_episodes_fts),e.id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$query", lexical.FullText);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$limit", request.Limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var score = Math.Max(0.1, 1 - 0.02 * offset);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Candidate();
                rows++;
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(budget.Payload(reader.GetString(0)), JsonOptions)!;
                if (!MemoryProvenance.IsSnapshotCurrent(episode, request.Partition, episode.Id, asOf)) continue;
                results.Add(new MemoryCandidate(episode.Id, MemoryLayer.Episodic, episode.Content, score,
                    SourceTrust(episode.Source.Type), MemoryConfirmationState.NotRequired, episode.Sensitivity,
                    episode.OccurredAt, episode.ExpiresAt, [episode.Id], "fulltext"));
                score = Math.Max(0.1, score - 0.02);
            }
        }
        if (Included(request, MemoryLayer.Semantic))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.payload,e.payload,p.payload,o.payload FROM memory_claims c
                JOIN memory_entities e ON e.id=c.subject_id AND e.partition_key=c.partition_key
                JOIN memory_episodes p ON p.id=c.episode_id AND p.partition_key=c.partition_key
                LEFT JOIN memory_entities o ON o.id=json_extract(c.payload,'$.objectEntityId') AND o.partition_key=c.partition_key
                WHERE c.partition_key=$partition AND (csweet_utc_ticks(c.valid_from)<=$now) AND ($superseded=1 OR c.valid_to IS NULL OR csweet_utc_ticks(c.valid_to)>$now)
                  AND c.confirmation IN (0,2,CASE WHEN $pending=1 THEN 1 ELSE 2 END)
                  AND {SourceHeadersEligible("c.payload", "c.partition_key")}
                  AND {SourceHeadersEligible("e.payload", "e.partition_key")}
                  AND (json_extract(c.payload,'$.objectEntityId') IS NULL OR (o.id IS NOT NULL AND {SourceHeadersEligible("o.payload", "o.partition_key")}))
                  AND COALESCE(json_extract(p.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(p.occurred_at)<=$now AND (p.expires_at IS NULL OR csweet_utc_ticks(p.expires_at)>$now)
                  AND (c.id IN (SELECT id FROM memory_claims_fts WHERE content MATCH $query)
                    OR c.subject_id IN (SELECT id FROM memory_entities_fts WHERE content MATCH $query)
                    OR json_extract(c.payload,'$.objectEntityId') IN (SELECT id FROM memory_entities_fts WHERE content MATCH $query))
                ORDER BY (lower(e.canonical_name)=$phrase OR lower(o.canonical_name)=$phrase) DESC,
                    instr(lower(c.predicate||' '||coalesce(c.value,'')),$phrase)>0 DESC,c.id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$superseded", request.IncludeSuperseded ? 1 : 0);
            command.Parameters.AddWithValue("$pending", request.IncludePending ? 1 : 0);
            command.Parameters.AddWithValue("$phrase", string.Join(' ', lexical.Terms));
            command.Parameters.AddWithValue("$query", lexical.FullText);
            command.Parameters.AddWithValue("$limit", request.Limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Candidate();
                rows++;
                var claim = JsonSerializer.Deserialize<MemoryClaim>(budget.Payload(reader.GetString(0)), JsonOptions)!;
                var subject = JsonSerializer.Deserialize<MemoryEntity>(budget.Payload(reader.GetString(1)), JsonOptions)!;
                var source = JsonSerializer.Deserialize<MemoryEpisode>(budget.Payload(reader.GetString(2)), JsonOptions)!;
                var objectEntity = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<MemoryEntity>(budget.Payload(reader.GetString(3)), JsonOptions);
                var resolved = MemoryProvenance.ResolveClaimReferences(claim, source, subject, objectEntity, asOf, snapshotOnly: true);
                if (claim.Partition != request.Partition || resolved is null || !MemoryProvenance.HasBoundedSources(claim.SourceEpisodeIds) ||
                    !HasBoundedLineage(subject) || (objectEntity is not null && !HasBoundedLineage(objectEntity))) continue;
                claim = resolved;
                var content = $"{subject.CanonicalName} {claim.Predicate} {claim.Value ?? objectEntity?.CanonicalName}";
                results.Add(new MemoryCandidate(claim.Id, MemoryLayer.Semantic, content, claim.Confidence * claim.Importance,
                    claim.Trust, claim.Confirmation, claim.Sensitivity, claim.ValidFrom, claim.ValidTo,
                    new[] { claim.EpisodeId }.Concat(claim.SourceEpisodeIds).Concat(subject.SourceEpisodeIds).Concat(objectEntity?.SourceEpisodeIds ?? []).Distinct().ToArray(), "semantic"));
            }

            await reader.DisposeAsync();

        }

        if (Included(request, MemoryLayer.Procedural))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT procedure.payload,source.payload FROM memory_procedures procedure
                JOIN memory_episodes source ON source.id=procedure.episode_id AND source.partition_key=procedure.partition_key
                WHERE procedure.partition_key=$partition AND procedure.confirmation IN (0,2)
                    AND {SourceHeadersEligible("procedure.payload", "procedure.partition_key")}
                    AND csweet_utc_ticks(procedure.valid_from)<=$now AND (procedure.valid_to IS NULL OR csweet_utc_ticks(procedure.valid_to)>$now)
                    AND COALESCE(json_extract(source.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(source.occurred_at)<=$now AND (source.expires_at IS NULL OR csweet_utc_ticks(source.expires_at)>$now)
                    AND procedure.id IN (SELECT id FROM memory_procedures_fts WHERE content MATCH $query)
                ORDER BY (lower(procedure.name)=$phrase) DESC,
                    instr(lower(json_extract(procedure.payload,'$.procedure')),$phrase)>0 DESC,procedure.id LIMIT $limit OFFSET $offset
                """;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$query", lexical.FullText);
            command.Parameters.AddWithValue("$phrase", string.Join(' ', lexical.Terms));
            command.Parameters.AddWithValue("$limit", request.Limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                budget.Candidate();
                rows++;
                var procedure = JsonSerializer.Deserialize<ProceduralMemory>(budget.Payload(reader.GetString(0)), JsonOptions)!;
                var source = JsonSerializer.Deserialize<MemoryEpisode>(budget.Payload(reader.GetString(1)), JsonOptions);
                if (procedure.Partition != request.Partition || !MemoryProvenance.HasBoundedSources(procedure.SourceEpisodeIds) ||
                    !MemoryProvenance.IsSnapshotCurrent(source, request.Partition, procedure.EpisodeId, asOf)) continue;
                results.Add(new MemoryCandidate(procedure.Id, MemoryLayer.Procedural, procedure.Procedure, 1,
                    procedure.Trust, procedure.Confirmation, source!.Sensitivity,
                    procedure.ValidFrom, procedure.ValidTo, new[] { procedure.EpisodeId }.Concat(procedure.SourceEpisodeIds).Distinct().ToArray(), "procedure"));
            }
        }
        return new(results, rows);
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
        command.CommandText = "SELECT payload FROM memory_claims WHERE id=$id AND partition_key LIKE 'mp2:%'";
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
        await ResolveTransferEpisodesAsync(await ListPayloadsAsync<MemoryEpisode>("memory_episodes", partition, cancellationToken), DateTimeOffset.UtcNow, cancellationToken),
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
        command.CommandText = "SELECT payload FROM memory_transfers WHERE id=$id AND NOT EXISTS(SELECT 1 FROM memory_partition_migration_rows WHERE table_name='memory_transfers' AND record_id=$id AND disposition='Quarantine')";
        command.Parameters.AddWithValue("$id", packageId.ToString("D"));
        return Deserialize<KnowledgeTransferPackage>(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var barrier = connection.CreateCommand())
        {
            barrier.Transaction = (SqliteTransaction)transaction;
            barrier.CommandText = "UPDATE memory_episodes SET legal_hold=legal_hold WHERE 1=0";
            await barrier.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var held = connection.CreateCommand())
        {
            held.Transaction = (SqliteTransaction)transaction;
            held.CommandText = "SELECT EXISTS(SELECT 1 FROM memory_episodes WHERE partition_key=$partition AND (legal_hold<>0 OR json_extract(payload,'$.legalHold')=1))";
            held.Parameters.AddWithValue("$partition", partition.StorageKey);
            if (Convert.ToInt64(await held.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("memory_legal_hold_prevents_deletion");
        }
        await new MemoryTransferEvidenceStorage(sql =>
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction; command.CommandText = sql; return command;
        }, false, DateTimeOffset.UtcNow).EnsureDeletionAllowedAsync(partition, cancellationToken);
        foreach (var table in new[] { "memory_uses", "memory_embeddings", "memory_procedures", "memory_blocks", "memory_edges", "memory_claims", "memory_entities", "memory_episodes", "memory_revisions" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = $"DELETE FROM {table} WHERE partition_key=$partition";
            command.Parameters.AddWithValue("$partition", partition.StorageKey);
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
        connection.CreateFunction<string?, long?>("csweet_utc_ticks", ParseUtcTicks, isDeterministic: true);
        return connection;
    }

    private async Task<MemoryWriteResult> InsertPayloadAsync(string table, Guid id, MemoryPartition partition, string payload, CancellationToken cancellationToken, params (string Name, string? Value)[] values)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var names = string.Join(',', values.Select(value => value.Name));
        var parameters = string.Join(',', values.Select(value => '$' + value.Name));
        command.CommandText = $"INSERT INTO {table}(id,partition_key,{names},payload) VALUES($id,$partition,{parameters},$payload) ON CONFLICT(id) DO NOTHING RETURNING id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
        command.Parameters.AddWithValue("$payload", payload);
        foreach (var value in values) command.Parameters.AddWithValue('$' + value.Name, Db(value.Value));
        if (await command.ExecuteScalarAsync(cancellationToken) is string)
            return new MemoryWriteResult(id, true);
        await using var replay = connection.CreateCommand();
        replay.CommandText = $"SELECT payload FROM {table} WHERE id=$id AND partition_key=$partition";
        replay.Parameters.AddWithValue("$id", id.ToString("D"));
        replay.Parameters.AddWithValue("$partition", partition.StorageKey);
        if (await replay.ExecuteScalarAsync(cancellationToken) is not string existing)
            throw new InvalidOperationException("memory_write_conflict");
        var previous = System.Text.Json.Nodes.JsonNode.Parse(existing)!.AsObject();
        var proposed = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        if (table != "memory_embeddings")
        {
            // Legacy single-source records omitted this additive field. Never normalize
            // a populated (or explicitly invalid null) list away during replay.
            if (!previous.ContainsKey("sourceEpisodeIds")) previous["sourceEpisodeIds"] = new System.Text.Json.Nodes.JsonArray();
            if (!proposed.ContainsKey("sourceEpisodeIds")) proposed["sourceEpisodeIds"] = new System.Text.Json.Nodes.JsonArray();
        }
        previous.Remove("recordedAt");
        proposed.Remove("recordedAt");
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(previous, proposed))
            throw new InvalidOperationException("memory_write_conflict");
        return new MemoryWriteResult(id, false, "Idempotent replay.");
    }

    private async Task UpdateClaimAsync(MemoryClaim claim, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_claims SET confirmation=$confirmation,valid_to=$validTo,payload=$payload WHERE id=$id";
        command.Parameters.AddWithValue("$confirmation", (int)claim.Confirmation);
        command.Parameters.AddWithValue("$validTo", Db(claim.ValidTo?.ToUniversalTime().ToString("O")));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(claim, JsonOptions));
        command.Parameters.AddWithValue("$id", claim.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<T>> ListPayloadsAsync<T>(string table, MemoryPartition partition, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM {table} WHERE partition_key=$partition";
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
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
        command.Parameters.AddWithValue("$partition", partition.StorageKey);
        command.Parameters.AddWithValue("$tenant", partition.TenantId);
        command.Parameters.AddWithValue("$application", Db(partition.ApplicationId));
        command.Parameters.AddWithValue("$agent", Db(partition.AgentId));
        command.Parameters.AddWithValue("$user", Db(partition.UserId));
        command.Parameters.AddWithValue("$conversation", Db(partition.ConversationId));
        command.Parameters.AddWithValue("$custom", Db(partition.CustomNamespace));
    }

    private static string IndexedSearchMigration
    {
        get
        {
            var sql = new System.Text.StringBuilder("BEGIN IMMEDIATE; CREATE TABLE IF NOT EXISTS memory_schema_migrations(id TEXT PRIMARY KEY);");
            foreach (var (table, expression) in new[]
            {
                ("memory_entities", "canonical_name || ' ' || coalesce(json_extract(payload,'$.aliases'),'')"),
                ("memory_claims", "predicate || ' ' || coalesce(value,'')"),
                ("memory_procedures", "name || ' ' || coalesce(json_extract(payload,'$.procedure'),'') || ' ' || coalesce(json_extract(payload,'$.applicability'),'')")
            })
            {
                // All identifiers/expressions are compile-time constants. Triggers also keep
                // legacy writers synchronized after the one-time, transactional backfill.
                var forNew = expression.Replace("canonical_name", "new.canonical_name").Replace("predicate", "new.predicate")
                    .Replace("value", "new.value").Replace("payload", "new.payload");
                if (table == "memory_procedures") forNew = forNew.Replace("name ||", "new.name ||");
                sql.Append($"CREATE VIRTUAL TABLE IF NOT EXISTS {table}_fts USING fts5(id UNINDEXED,content);");
                sql.Append($"CREATE TRIGGER IF NOT EXISTS {table}_fts_insert AFTER INSERT ON {table} BEGIN INSERT INTO {table}_fts(id,content) VALUES(new.id,{forNew}); END;");
                sql.Append($"CREATE TRIGGER IF NOT EXISTS {table}_fts_update AFTER UPDATE ON {table} BEGIN DELETE FROM {table}_fts WHERE id=old.id; INSERT INTO {table}_fts(id,content) VALUES(new.id,{forNew}); END;");
                sql.Append($"CREATE TRIGGER IF NOT EXISTS {table}_fts_delete AFTER DELETE ON {table} BEGIN DELETE FROM {table}_fts WHERE id=old.id; END;");
                sql.Append($"INSERT INTO {table}_fts(id,content) SELECT id,{expression} FROM {table} WHERE NOT EXISTS(SELECT 1 FROM memory_schema_migrations WHERE id='indexed-search-v1');");
            }
            sql.Append("INSERT OR IGNORE INTO memory_schema_migrations(id) VALUES('indexed-search-v1'); COMMIT;");
            return sql.ToString();
        }
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
