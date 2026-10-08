using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSweet.Memory;

internal static partial class MemoryErasureStorage
{
    private static readonly string[] Tables = ["episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings", "uses", "transfers"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumSnapshots = 16_384;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private sealed record Key(MemoryErasureKind Kind, Guid Id, MemoryPartition Partition);
    private sealed record Snapshot(Key Key, string Payload, long Revision, bool Current, HashSet<Guid> References,
        HashSet<Guid> Required, string? SourceType, string? SourceId, bool Held, bool Unresolved, bool SourceLess,
        IReadOnlyList<MemoryPartition> TransferScopes);
    private sealed record Plan(MemoryErasurePreview Preview, IReadOnlyList<Snapshot> Snapshots);
    private static string Prefix(bool postgres) => postgres ? "csweet_memory_" : "memory_";

    internal static async Task<MemoryErasurePreview> PreviewAsync(DbConnection connection, DbTransaction transaction, bool postgres,
        MemoryPartition partition, Guid episodeId, CancellationToken token)
    {
        await BarrierAsync(connection, transaction, postgres, token);
        return (await BuildAsync(connection, transaction, postgres, partition, episodeId, token)).Preview;
    }

    internal static async Task<MemoryErasureResult> EraseAsync(DbConnection connection, DbTransaction transaction, bool postgres,
        MemoryPartition partition, Guid episodeId, string evidenceToken, CancellationToken token)
    {
        if (episodeId == Guid.Empty || evidenceToken?.Length != 64) throw new ArgumentException("memory_erasure_request_invalid");
        await BarrierAsync(connection, transaction, postgres, token);
        var prefix = Prefix(postgres);
        await using (var replay = Command(connection, transaction, $"SELECT CAST(payload AS text) FROM {prefix}erasure_receipts WHERE partition_key=@partition AND episode_id=@id"))
        {
            Add(replay, "partition", partition.StorageKey); Add(replay, "id", DbId(postgres, episodeId));
            if (await replay.ExecuteScalarAsync(token) is string receipt)
            {
                var saved = JsonSerializer.Deserialize<MemoryErasureResult>(receipt, Json)!;
                if (saved.Partition != partition || saved.EpisodeId != episodeId || saved.EvidenceToken != evidenceToken)
                    throw new InvalidOperationException("memory_erasure_review_changed");
                return saved with { WasReplay = true };
            }
        }
        var plan = await BuildAsync(connection, transaction, postgres, partition, episodeId, token);
        if (plan.Preview.EvidenceToken != evidenceToken) throw new InvalidOperationException("memory_erasure_review_changed");
        if (plan.Preview.BlockedReason is { } reason) throw new InvalidOperationException(reason);
        var targets = plan.Preview.Targets;
        var erasedRecords = 0; var erasedRevisions = 0;
        foreach (var target in targets)
        {
            await using var delete = Command(connection, transaction, $"DELETE FROM {prefix}{Tables[(int)target.Kind]} WHERE id=@id");
            Add(delete, "id", DbId(postgres, target.Id));
            erasedRecords += await delete.ExecuteNonQueryAsync(token);
        }
        // Delete triggers retain a final snapshot. Purge history only after all live rows,
        // in this same transaction, so no final source payload remains in the history table.
        foreach (var target in targets)
        {
            if ((int)target.Kind < 7)
            {
                await using var history = Command(connection, transaction, $"DELETE FROM {prefix}revisions WHERE partition_key=@partition AND kind=@kind AND record_id=@id");
                Bind(history, target); erasedRevisions += await history.ExecuteNonQueryAsync(token);
            }
            var versions = plan.Snapshots.Where(x => x.Key == new Key(target.Kind, target.Id, target.Partition)).ToArray();
            var source = versions.OrderByDescending(x => x.Current).ThenByDescending(x => x.Revision).First();
            await using var fence = Command(connection, transaction, $"""
                INSERT INTO {prefix}erased_records(partition_key,kind,record_id,tenant_id,application_id,source_type,source_id)
                    VALUES(@partition,@kind,@id,@tenant,@application,@type,@source) ON CONFLICT(partition_key,kind,record_id) DO NOTHING
                """);
            Bind(fence, target); Add(fence, "tenant", target.Partition.TenantId); Add(fence, "application", target.Partition.ApplicationId);
            Add(fence, "type", source.SourceType); Add(fence, "source", source.SourceId);
            await fence.ExecuteNonQueryAsync(token);
            if (target.Kind == MemoryErasureKind.Episode)
            {
                foreach (var identity in versions.Select(x => (x.SourceType, x.SourceId)).Distinct())
                {
                    await using var logical = Command(connection, transaction, $"""
                        INSERT INTO {prefix}erased_sources(partition_key,tenant_id,application_id,source_type,source_id)
                            VALUES(@partition,@tenant,@application,@type,@source) ON CONFLICT(partition_key,source_type,source_id) DO NOTHING
                        """);
                    Add(logical, "partition", target.Partition.StorageKey); Add(logical, "tenant", target.Partition.TenantId);
                    Add(logical, "application", target.Partition.ApplicationId); Add(logical, "type", identity.SourceType!); Add(logical, "source", identity.SourceId!);
                    await logical.ExecuteNonQueryAsync(token);
                    // The erasure ledger fences current logical identity across audiences;
                    // existing suppression tombstones also preserve partition-local history.
                    await using var suppress = Command(connection, transaction, $"""
                        INSERT INTO {prefix}suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                            VALUES(@partition,@type,@source,@id,@now) ON CONFLICT(partition_key,episode_id) DO NOTHING
                        """);
                    Add(suppress, "partition", target.Partition.StorageKey); Add(suppress, "id", DbId(postgres, target.Id));
                    Add(suppress, "type", identity.SourceType ?? "unverifiable"); Add(suppress, "source", identity.SourceId ?? target.Id.ToString("D"));
                    Add(suppress, "now", postgres ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.ToString("O"));
                    await suppress.ExecuteNonQueryAsync(token);
                }
            }
        }
        var result = new MemoryErasureResult(partition, episodeId, evidenceToken, erasedRecords, erasedRevisions, DateTimeOffset.UtcNow, false);
        await using var write = Command(connection, transaction, $"INSERT INTO {prefix}erasure_receipts(partition_key,episode_id,payload) VALUES(@partition,@id,{(postgres ? "CAST(@payload AS jsonb)" : "@payload")})");
        Add(write, "partition", partition.StorageKey); Add(write, "id", DbId(postgres, episodeId)); Add(write, "payload", JsonSerializer.Serialize(result, Json));
        await write.ExecuteNonQueryAsync(token);
        return result;

        void Bind(DbCommand command, MemoryErasureTarget target)
        { Add(command, "partition", target.Partition.StorageKey); Add(command, "kind", (int)target.Kind); Add(command, "id", DbId(postgres, target.Id)); }
    }

    private static async Task<Plan> BuildAsync(DbConnection connection, DbTransaction transaction, bool postgres,
        MemoryPartition partition, Guid episodeId, CancellationToken token)
    {
        if (episodeId == Guid.Empty) throw new ArgumentException("memory_erasure_request_invalid");
        var prefix = Prefix(postgres); var rows = new List<Snapshot>(); var bytes = 0;
        for (var kind = 0; kind < Tables.Length; kind++)
        {
            var tenant = kind == 8 ? "tenant_id" : postgres ? "payload->'partition'->>'tenantId'" : "json_extract(payload,'$.partition.tenantId')";
            var route = kind == 8 ? "NULL" : "partition_key";
            var indexedHold = !postgres && kind == 0 ? "legal_hold" : "0";
            var normalizedType = kind == 0 ? SourceTypeSql(postgres) : "NULL";
            await Read($"SELECT id,{route},CAST(payload AS text),0,{indexedHold},{normalizedType} FROM {prefix}{Tables[kind]} WHERE {tenant}=@tenant LIMIT {MaximumSnapshots + 1}", (MemoryErasureKind)kind, true);
        }
        var historyTenant = postgres ? "payload->'partition'->>'tenantId'" : "json_extract(payload,'$.partition.tenantId')";
        await using (var command = Command(connection, transaction, $"SELECT record_id,partition_key,CAST(payload AS text),revision,kind,CASE WHEN kind=0 THEN {SourceTypeSql(postgres)} ELSE NULL END FROM {prefix}revisions WHERE {historyTenant}=@tenant ORDER BY revision LIMIT {MaximumSnapshots + 1}"))
        {
            Add(command, "tenant", partition.TenantId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) AddRow(reader, (MemoryErasureKind)reader.GetInt32(4), false);
        }
        var root = new Key(MemoryErasureKind.Episode, episodeId, partition);
        var rootRows = rows.Where(x => x.Key == root).ToArray();
        if (rootRows.Length == 0) throw new KeyNotFoundException();
        var selected = new HashSet<Key> { root };
        var rootIdentities = rootRows.Select(x => (x.SourceType, x.SourceId)).ToHashSet();
        foreach (var row in rows.Where(x => x.Key.Kind == MemoryErasureKind.Episode && x.Key.Partition.ApplicationId == partition.ApplicationId &&
            x.SourceType is not null && x.SourceId is not null && rootIdentities.Contains((x.SourceType, x.SourceId)))) selected.Add(row.Key);
        Expand(selected, forward: true);
        var dependencies = selected.ToHashSet(); Expand(dependencies, forward: false);
        var latest = rows.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Current).ThenByDescending(y => y.Revision).First());
        var knownIds = rows.Select(x => x.Key.Id).ToHashSet();
        var versionsByKey = rows.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.ToArray());
        string? blocked = null;
        var transferEvidence = new MemoryTransferEvidenceStorage(sql => Command(connection, transaction, sql), postgres, DateTimeOffset.UtcNow);
        foreach (var key in dependencies)
        {
            var current = latest[key];
            if (current.Held) { blocked = "memory_legal_hold_prevents_deletion"; break; }
            var unresolved = current.Unresolved;
            if (key.Kind == MemoryErasureKind.Episode)
            {
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(current.Payload, Json)!;
                if ((episode.CorrectionEvidence is not null || episode.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true) &&
                    !await transferEvidence.VerifyCorrectionRetentionAsync(episode, token)) unresolved = true;
            }
            if (unresolved && key.Kind == MemoryErasureKind.Episode && current.SourceType == "knowledge-transfer")
            {
                var copy = JsonSerializer.Deserialize<MemoryEpisode>(current.Payload, Json)!;
                using var payload = JsonDocument.Parse(current.Payload);
                // Empty selected records are valid for an approved notes-only handoff.
                // Require its live package, exact sealed certificate and canonical
                // audience closure instead of treating an empty list as proof itself.
                if (copy.TransferEvidence?.Records is { Count: 0 } && MemorySourceIntegrity.IsVerified(copy) &&
                    payload.RootElement.TryGetProperty("legalHold", out var legalHold) && legalHold.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    unresolved = !await transferEvidence.VerifyNotesOnlyRetentionAsync(copy, token);
            }
            if (unresolved || versionsByKey[key].Any(x => x.Required.Any(id => !knownIds.Contains(id))))
                blocked = "memory_erasure_lineage_review_required";
        }
        var affectedPartitions = selected.Select(x => x.Partition).ToHashSet();
        // Unlinked legacy derivatives may contain the requested source. Do not claim a
        // complete erasure merely because the older record omitted its source links.
        if (blocked is null && rows.Any(x => x.SourceLess && affectedPartitions.Contains(x.Key.Partition)))
            blocked = "memory_erasure_lineage_review_required";
        var targets = selected.OrderBy(x => (int)x.Kind).ThenBy(x => x.Partition.StorageKey, StringComparer.Ordinal).ThenBy(x => x.Id)
            .Select(x => new MemoryErasureTarget(x.Kind, x.Id, x.Partition)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Hash(string value)
        {
            var data = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            hash.AppendData(length); hash.AppendData(data);
        }
        Hash(Version); Hash(partition.StorageKey); Hash(episodeId.ToString("D")); Hash(blocked ?? "");
        // Bind both reviewed content and retention dependencies, including historical
        // revisions. An unrelated tenant cannot invalidate or disclose this preview.
        foreach (var row in rows.OrderBy(x => (int)x.Key.Kind).ThenBy(x => x.Key.Partition.StorageKey, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Id).ThenBy(x => x.Current).ThenBy(x => x.Revision))
        { Hash(JsonSerializer.Serialize(row.Key, Json)); Hash(row.Current.ToString()); Hash(row.Held.ToString()); Hash(row.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)); Hash(row.Payload); }
        return new(new(partition, episodeId, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), targets, blocked), rows);

        void Expand(HashSet<Key> keys, bool forward)
        {
            bool changed; var passes = 0;
            do
            {
                if (++passes > 128) throw new InvalidOperationException("memory_erasure_scan_limit");
                token.ThrowIfCancellationRequested(); changed = false;
                var ids = keys.Select(x => x.Id).ToHashSet(); var scopes = keys.Select(x => x.Partition).ToHashSet();
                if (forward)
                {
                    var identities = rows.Where(x => keys.Contains(x.Key) && x.Key.Kind == MemoryErasureKind.Episode)
                        .Select(x => (x.Key.Partition.ApplicationId, x.SourceType, x.SourceId)).ToHashSet();
                    foreach (var row in rows)
                        if (ids.Contains(row.Key.Id) || row.References.Overlaps(ids) || row.TransferScopes.Any(scopes.Contains) ||
                            (row.Key.Kind == MemoryErasureKind.Episode && row.SourceType is not null && row.SourceId is not null &&
                                identities.Contains((row.Key.Partition.ApplicationId, row.SourceType, row.SourceId)))) changed |= keys.Add(row.Key);
                }
                else
                {
                    var needed = rows.Where(x => keys.Contains(x.Key)).SelectMany(x => x.References).ToHashSet();
                    var transferScopes = rows.Where(x => keys.Contains(x.Key)).SelectMany(x => x.TransferScopes).ToHashSet();
                    foreach (var row in rows)
                        if (needed.Contains(row.Key.Id) || transferScopes.Contains(row.Key.Partition)) changed |= keys.Add(row.Key);
                }
            } while (changed);
        }
        async Task Read(string sql, MemoryErasureKind kind, bool current)
        {
            await using var command = Command(connection, transaction, sql); Add(command, "tenant", partition.TenantId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) AddRow(reader, kind, current);
        }
        void AddRow(DbDataReader reader, MemoryErasureKind kind, bool current)
        {
            var payload = reader.GetString(2); bytes += Encoding.UTF8.GetByteCount(payload);
            if (rows.Count >= MaximumSnapshots || bytes > MaximumBytes) throw new InvalidOperationException("memory_erasure_scan_limit");
            var id = reader.GetValue(0) is Guid guid ? guid : Guid.Parse(reader.GetString(0));
            var snapshot = Parse(id, kind, reader.IsDBNull(1) ? null : reader.GetString(1), payload, Convert.ToInt64(reader.GetValue(3)), current, partition.TenantId);
            // Use the same database case rules as the durable source guard, including
            // Unicode source types. Do not normalize once in CLR and again differently in SQL.
            if (kind == MemoryErasureKind.Episode) snapshot = snapshot with { SourceType = reader.IsDBNull(5) ? null : reader.GetString(5) };
            rows.Add(current && Convert.ToBoolean(reader.GetValue(4)) ? snapshot with { Held = true } : snapshot);
        }
    }

    private static Snapshot Parse(Guid id, MemoryErasureKind kind, string? route, string payload, long revision, bool current, string tenant)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload); var json = doc.RootElement;
            var partition = (kind == MemoryErasureKind.Transfer ? json.GetProperty("targetNamespace").GetProperty("partition") : json.GetProperty("partition"))
                .Deserialize<MemoryPartition>(Json) ?? throw new JsonException();
            if (json.GetProperty("id").GetGuid() != id || partition.TenantId != tenant || (route is not null && partition.StorageKey != route))
                throw new JsonException();
            var references = new HashSet<Guid>(); var required = new HashSet<Guid>();
            void Visit(JsonElement value, string? property = null)
            {
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var item in value.EnumerateObject()) Visit(item.Value, item.Name);
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Visit(item, property);
                else if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var referenced) && referenced != Guid.Empty)
                {
                    references.Add(referenced);
                    if (property is "episodeId" or "sourceEpisodeIds" or "episodeIds" or "subjectEntityId" or "objectEntityId" or "fromEntityId" or "toEntityId" or "memoryId")
                        required.Add(referenced);
                }
            }
            Visit(json); references.Remove(id);
            string? sourceType = null; string? sourceId = null; var held = false; var unresolved = false;
            if (kind == MemoryErasureKind.Episode)
            {
                sourceType = json.GetProperty("source").GetProperty("type").GetString()?.ToLowerInvariant();
                sourceId = json.GetProperty("source").GetProperty("id").GetString();
                unresolved = string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceId) || !json.TryGetProperty("legalHold", out _);
                held = json.TryGetProperty("legalHold", out var hold) && hold.GetBoolean();
                if (json.TryGetProperty("transferEvidence", out var transfer) && transfer.ValueKind == JsonValueKind.Object)
                {
                    held |= transfer.TryGetProperty("legalHold", out var transferHold) && transferHold.GetBoolean();
                    if (!transfer.TryGetProperty("records", out var records) || records.GetArrayLength() == 0) unresolved = true;
                    else foreach (var record in records.EnumerateArray()) required.Add(record.GetProperty("id").GetGuid());
                }
                else if (sourceType == "knowledge-transfer") unresolved = true;
            }
            var scopes = new List<MemoryPartition>();
            if (kind == MemoryErasureKind.Transfer)
            {
                foreach (var scope in json.GetProperty("sourceNamespaces").EnumerateArray())
                    scopes.Add(scope.GetProperty("partition").Deserialize<MemoryPartition>(Json)!);
                if (scopes.Any(x => x.TenantId != tenant)) unresolved = true;
                if (json.TryGetProperty("approvedEvidence", out var evidence) && evidence.ValueKind == JsonValueKind.Object)
                {
                    held = evidence.TryGetProperty("legalHold", out var hold) && hold.GetBoolean();
                    foreach (var record in evidence.GetProperty("records").EnumerateArray()) required.Add(record.GetProperty("id").GetGuid());
                }
            }
            var sourceLess = kind is MemoryErasureKind.Entity or MemoryErasureKind.Claim or MemoryErasureKind.Edge or MemoryErasureKind.Block or MemoryErasureKind.Procedure &&
                (!json.TryGetProperty("sourceEpisodeIds", out var sources) || sources.GetArrayLength() == 0) &&
                (!json.TryGetProperty("episodeId", out var original) || original.GetGuid() == Guid.Empty);
            return new(new(kind, id, partition), payload, revision, current, references, required, sourceType, sourceId, held, unresolved, sourceLess, scopes);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidOperationException("memory_erasure_lineage_review_required", error); }
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command; }
    private static object DbId(bool postgres, Guid id) => postgres ? id : id.ToString("D");
    private static string SourceTypeSql(bool postgres) => postgres ? "lower(payload->'source'->>'type')" : "lower(json_extract(payload,'$.source.type'))";
    private static void Add(DbCommand command, string name, object? value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
}
