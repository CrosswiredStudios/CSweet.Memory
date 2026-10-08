using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CSweet.Memory;

public sealed record MemoryPartitionMigrationRow(string Table, Guid Id, string PreviousKey,
    string DestinationKey, MemoryPartition? Partition, string Disposition, string Reason, string PayloadHash);

/// <summary>Content-free inventory. Treat namespace identifiers as administrative data.</summary>
public sealed record MemoryPartitionMigrationPlan(string Version, string Fingerprint,
    IReadOnlyList<MemoryPartitionMigrationRow> Rows)
{
    public const string CurrentVersion = "canonical-partitions-v2";
}

public interface IMemoryPartitionMigration
{
    Task<MemoryPartitionMigrationPlan> InspectPartitionMigrationAsync(CancellationToken cancellationToken = default);
    Task ApplyPartitionMigrationAsync(string expectedFingerprint, CancellationToken cancellationToken = default);
}

/// <summary>Trusted offline migration implementation shared by the database stores, not an authorization API.</summary>
internal static class MemoryPartitionMigration
{
    public const string Version = MemoryPartitionMigrationPlan.CurrentVersion;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Kinds = ["episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings", "uses"];
    private const int MaximumRows = 100_000;

    private static string Prefix(bool postgres) => postgres ? "csweet_memory_" : "memory_";

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public static async Task EnsureMetadataAsync(DbConnection connection, DbTransaction? transaction, bool postgres, CancellationToken token)
    {
        var prefix = Prefix(postgres);
        await using var command = Command(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {prefix}partition_migration_rows(
                table_name text NOT NULL,record_id text NOT NULL,previous_key text NOT NULL,destination_key text NOT NULL,
                disposition text NOT NULL,reason text NOT NULL,plan_fingerprint text NOT NULL,payload_hash text NOT NULL,
                PRIMARY KEY(table_name,record_id));
            CREATE TABLE IF NOT EXISTS {prefix}partition_migration_runs(version text PRIMARY KEY,fingerprint text NOT NULL,plan_json text NOT NULL);
            """);
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task<bool> IsAppliedAsync(DbConnection connection, DbTransaction? transaction, bool postgres, CancellationToken token)
    {
        await using var command = Command(connection, transaction,
            $"SELECT count(*) FROM {Prefix(postgres)}schema_migrations WHERE id=@version", ("version", Version));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
    }

    public static async Task<MemoryPartitionMigrationPlan> InspectAsync(DbConnection connection, DbTransaction? transaction,
        bool postgres, CancellationToken token)
    {
        var prefix = Prefix(postgres);
        if (await IsAppliedAsync(connection, transaction, postgres, token))
        {
            await using var applied = Command(connection, transaction,
                $"SELECT plan_json FROM {prefix}partition_migration_runs WHERE version=@version", ("version", Version));
            var saved = await applied.ExecuteScalarAsync(token) as string
                ?? throw new InvalidOperationException("memory_migration_receipt_missing");
            return JsonSerializer.Deserialize<MemoryPartitionMigrationPlan>(saved, JsonOptions)!;
        }
        var rows = new List<MemoryPartitionMigrationRow>();
        foreach (var kind in Kinds)
        {
            var table = prefix + kind;
            var indexedFields = !postgres && kind == "episodes"
                ? ",tenant_id,application_id,agent_id,user_id,conversation_id,custom_namespace" : "";
            await using var command = Command(connection, transaction,
                $"SELECT CAST(id AS text),partition_key,CAST(payload AS text){indexedFields} FROM {table} ORDER BY id LIMIT {MaximumRows + 1}");
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (rows.Count >= MaximumRows) throw new InvalidOperationException("memory_migration_inventory_limit");
                var id = Guid.Parse(reader.GetString(0));
                var previous = reader.GetString(1);
                var payload = reader.GetString(2);
                MemoryPartition? partition = null;
                var verified = false;
                try
                {
                    using var json = JsonDocument.Parse(payload);
                    partition = json.RootElement.GetProperty("partition").Deserialize<MemoryPartition>(JsonOptions);
                    _ = partition?.StorageKey; // Reject invalid UTF-16 rather than hash replacement characters.
                    verified = json.RootElement.TryGetProperty("id", out var storedId) && storedId.TryGetGuid(out var parsedId) && parsedId == id;
                    if (kind is "episodes" or "entities" or "claims" or "blocks")
                        verified &= json.RootElement.TryGetProperty("sensitivity", out var sensitivity) &&
                            sensitivity.TryGetInt32(out var classification) && Enum.IsDefined((MemorySensitivity)classification);
                    if (partition is not null && indexedFields.Length != 0)
                    {
                        string?[] fields = [partition.TenantId, partition.ApplicationId, partition.AgentId,
                            partition.UserId, partition.ConversationId, partition.CustomNamespace];
                        for (var index = 0; index < fields.Length; index++)
                            verified &= (reader.IsDBNull(index + 3) ? null : reader.GetString(index + 3)) == fields[index];
                    }
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or EncoderFallbackException)
                { partition = null; }
                var valid = verified && partition is not null && !string.IsNullOrWhiteSpace(partition.TenantId) &&
                    (previous == partition.Key || previous == partition.StorageKey);
                rows.Add(new(table, id, previous, valid ? partition!.StorageKey : $"quarantine:mp2:{kind}:{id:N}",
                    partition, valid ? "Migrate" : "Quarantine", valid ? "exact-fields" : "unverifiable-identity", Hash(payload)));
            }
        }
        // A flattened key used by multiple identities is not evidence of ownership, even
        // when individual payloads appear to explain it. Preserve the whole group for review.
        var collisions = rows.GroupBy(x => x.PreviousKey, StringComparer.Ordinal)
            .Where(group => group.Any(x => x.Disposition == "Quarantine") ||
                group.Select(x => x.Partition?.StorageKey).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        rows = rows.Select(row => collisions.Contains(row.PreviousKey)
            ? row with { DestinationKey = $"quarantine:mp2:{row.Table}:{row.Id:N}", Disposition = "Quarantine", Reason = "legacy-key-collision" }
            : row).ToList();
        var merges = rows.Where(row => row.Disposition == "Migrate").GroupBy(row => row.DestinationKey, StringComparer.Ordinal)
            .Where(group => group.Select(row => row.PreviousKey).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        rows = rows.Select(row => merges.Contains(row.DestinationKey)
            ? row with { DestinationKey = $"quarantine:mp2:{row.Table}:{row.Id:N}", Disposition = "Quarantine", Reason = "canonical-merge-requires-review" }
            : row).ToList();
        // Legacy transfer approvals have no migration review receipt. Retain them for
        // explicit review; do not let a previously copied debrief bypass source quarantine.
        await using (var command = Command(connection, transaction,
            $"SELECT CAST(id AS text),CAST(payload AS text) FROM {prefix}transfers ORDER BY id LIMIT {MaximumRows + 1}"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                if (rows.Count >= MaximumRows) throw new InvalidOperationException("memory_migration_inventory_limit");
                var id = Guid.Parse(reader.GetString(0));
                rows.Add(new(prefix + "transfers", id, "", "", null, "Quarantine", "transfer-review-required", Hash(reader.GetString(1))));
            }
        }
        return new(Version, Hash(JsonSerializer.Serialize(rows, JsonOptions)), rows);
    }

    /// <summary>Caller must hold the database writer locks and commit this operation atomically.</summary>
    public static async Task ApplyAsync(DbConnection connection, DbTransaction transaction, bool postgres,
        string expectedFingerprint, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        var prefix = Prefix(postgres);
        if (await IsAppliedAsync(connection, transaction, postgres, token))
        {
            await using var replay = Command(connection, transaction,
                $"SELECT fingerprint FROM {prefix}partition_migration_runs WHERE version=@version", ("version", Version));
            if (!string.Equals(await replay.ExecuteScalarAsync(token) as string, expectedFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("memory_migration_plan_changed");
            return;
        }
        var plan = await InspectAsync(connection, transaction, postgres, token);
        if (!string.Equals(expectedFingerprint, plan.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("memory_migration_plan_changed");
        foreach (var row in plan.Rows)
        {
            if (row.Table != prefix + "transfers")
            {
                await using var read = Command(connection, transaction,
                    $"SELECT CAST(payload AS text) FROM {row.Table} WHERE CAST(id AS text)=@id", ("id", row.Id.ToString("D")));
                var payload = (string)(await read.ExecuteScalarAsync(token))!;
                if (row.Disposition == "Migrate")
                {
                    var json = JsonNode.Parse(payload)!.AsObject();
                    json["partition"]!["storageKey"] = row.DestinationKey;
                    payload = json.ToJsonString();
                }
                await using var update = Command(connection, transaction,
                    $"UPDATE {row.Table} SET partition_key=@key,payload={(postgres ? "CAST(@payload AS jsonb)" : "@payload")} WHERE CAST(id AS text)=@id",
                    ("key", row.DestinationKey), ("payload", payload), ("id", row.Id.ToString("D")));
                await update.ExecuteNonQueryAsync(token);
            }
            await using var receipt = Command(connection, transaction, $"""
                INSERT INTO {prefix}partition_migration_rows(table_name,record_id,previous_key,destination_key,disposition,reason,plan_fingerprint,payload_hash)
                VALUES(@table,@id,@previous,@destination,@disposition,@reason,@fingerprint,@hash)
                """, ("table", row.Table), ("id", row.Id.ToString("D")), ("previous", row.PreviousKey),
                ("destination", row.DestinationKey), ("disposition", row.Disposition), ("reason", row.Reason),
                ("fingerprint", plan.Fingerprint), ("hash", row.PayloadHash));
            await receipt.ExecuteNonQueryAsync(token);
        }
        await InstallGuardsAsync(connection, transaction, postgres, token);
        await using var run = Command(connection, transaction,
            $"INSERT INTO {prefix}partition_migration_runs(version,fingerprint,plan_json) VALUES(@version,@fingerprint,@plan)",
            ("version", Version), ("fingerprint", plan.Fingerprint), ("plan", JsonSerializer.Serialize(plan, JsonOptions)));
        await run.ExecuteNonQueryAsync(token);
        await using var stamp = Command(connection, transaction,
            $"INSERT INTO {prefix}schema_migrations(id) VALUES(@version)", ("version", Version));
        await stamp.ExecuteNonQueryAsync(token);
    }

    public static async Task LockAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        if (!postgres) return; // SQLite callers acquire an immediate writer transaction.
        await using var command = Command(connection, transaction,
            "SELECT pg_advisory_xact_lock(728145092); LOCK TABLE " +
            string.Join(',', Kinds.Append("transfers").Select(kind => Prefix(true) + kind)) + " IN SHARE ROW EXCLUSIVE MODE");
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InstallGuardsAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        var prefix = Prefix(postgres);
        foreach (var kind in Kinds.Append("transfers"))
        {
            var table = prefix + kind;
            var condition = kind == "transfers"
                ? (postgres ? "NEW.payload #>> '{targetNamespace,partition,storageKey}' LIKE 'mp2:%'" : "json_extract(NEW.payload,'$.targetNamespace.partition.storageKey') LIKE 'mp2:%'")
                : (postgres ? "NEW.partition_key LIKE 'mp2:%' AND NEW.payload #>> '{partition,storageKey}' = NEW.partition_key"
                    : "NEW.partition_key LIKE 'mp2:%' AND json_extract(NEW.payload,'$.partition.storageKey')=NEW.partition_key");
            var notQuarantined = $"NOT EXISTS(SELECT 1 FROM {prefix}partition_migration_rows WHERE table_name='{table}' AND record_id=CAST(NEW.id AS text) AND disposition='Quarantine')";
            var sql = postgres ? $"""
                CREATE OR REPLACE FUNCTION {table}_canonical_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN IF NOT COALESCE(({condition}) AND ({notQuarantined}),false) THEN
                    RAISE EXCEPTION 'memory_canonical_partition_required'; END IF; RETURN NEW; END $guard$;
                CREATE TRIGGER {table}_canonical_guard BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION {table}_canonical_guard();
                """ : $"""
                CREATE TRIGGER {table}_canonical_insert BEFORE INSERT ON {table} WHEN NOT coalesce(({condition}) AND ({notQuarantined}),0)
                    BEGIN SELECT RAISE(ABORT,'memory_canonical_partition_required'); END;
                CREATE TRIGGER {table}_canonical_update BEFORE UPDATE ON {table} WHEN NOT coalesce(({condition}) AND ({notQuarantined}),0)
                    BEGIN SELECT RAISE(ABORT,'memory_canonical_partition_required'); END;
                """;
            await using var command = Command(connection, transaction, sql);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
