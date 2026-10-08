using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CSweet.Memory;

/// <summary>Shared database history upgrade and administrative projection, not an authorization boundary.</summary>
internal static class MemoryRevisionStorage
{
    internal const string Version = "revision-history-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] Tables = ["episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings"];
    private static string Prefix(bool postgres) => postgres ? "csweet_memory_" : "memory_";

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }

    internal static async Task EnsureAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        await EnsureHistoryAsync(connection, transaction, postgres, token);
        await EnsureSourceFingerprintsAsync(connection, transaction, postgres, token);
        await MemorySuppressionStorage.EnsureAsync(connection, transaction, postgres, token);
        await MemoryErasureStorage.EnsureAsync(connection, transaction, postgres, token);
    }

    private static async Task EnsureSourceFingerprintsAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        var prefix = Prefix(postgres);
        async Task<bool> Applied()
        {
            await using var check = Command(connection, transaction, $"SELECT count(*) FROM {prefix}schema_migrations WHERE id='source-fingerprints-v1'");
            return Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0;
        }
        if (await Applied()) return;
        if (postgres)
        {
            await using var guard = Command(connection, transaction,
                $"SELECT pg_advisory_xact_lock(728145092); LOCK TABLE {prefix}episodes IN SHARE ROW EXCLUSIVE MODE");
            await guard.ExecuteNonQueryAsync(token);
            if (await Applied()) return;
        }
        // Never bless old content by inventing a fingerprint during an automatic upgrade.
        // A reviewed legacy migration must establish its evidence separately.
        var sql = postgres ? $"""
            CREATE FUNCTION {prefix}source_fingerprint_guard() RETURNS trigger LANGUAGE plpgsql AS $integrity$
            BEGIN
                IF OLD.payload->>'sourceFingerprint' IS NOT NULL AND
                    OLD.payload->>'sourceFingerprint' IS DISTINCT FROM NEW.payload->>'sourceFingerprint' THEN
                    RAISE EXCEPTION 'memory_source_fingerprint_immutable';
                END IF;
                RETURN NEW;
            END $integrity$;
            CREATE TRIGGER {prefix}source_fingerprint_guard BEFORE UPDATE ON {prefix}episodes
                FOR EACH ROW EXECUTE FUNCTION {prefix}source_fingerprint_guard();
            """ : $"""
            CREATE TRIGGER {prefix}source_fingerprint_guard BEFORE UPDATE ON {prefix}episodes
                WHEN json_extract(OLD.payload,'$.sourceFingerprint') IS NOT NULL AND
                    json_extract(OLD.payload,'$.sourceFingerprint') IS NOT json_extract(NEW.payload,'$.sourceFingerprint')
                BEGIN SELECT RAISE(ABORT,'memory_source_fingerprint_immutable'); END;
            """;
        await using var install = Command(connection, transaction, sql + $"INSERT INTO {prefix}schema_migrations(id) VALUES('source-fingerprints-v1');");
        await install.ExecuteNonQueryAsync(token);
    }

    private static async Task EnsureHistoryAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        var prefix = Prefix(postgres);
        async Task<bool> Applied()
        {
            await using var check = Command(connection, transaction, $"SELECT count(*) FROM {prefix}schema_migrations WHERE id='{Version}'");
            return Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0;
        }
        if (await Applied()) return;
        if (postgres)
        {
            // Serialize upgrade with other initializers and existing writers, so baseline
            // snapshots and installed triggers form one atomic, gap-free boundary.
            await using var guard = Command(connection, transaction, "SELECT pg_advisory_xact_lock(728145092); LOCK TABLE " +
                string.Join(',', Tables.Select(x => prefix + x)) + " IN SHARE ROW EXCLUSIVE MODE");
            await guard.ExecuteNonQueryAsync(token);
            if (await Applied()) return;
        }
        var sql = new StringBuilder(postgres ? $"""
            CREATE TABLE {prefix}revisions(
                revision bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,partition_key text NOT NULL,
                kind integer NOT NULL,record_id uuid NOT NULL,operation integer NOT NULL,
                recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),payload jsonb NOT NULL);
            """ : $"""
            CREATE TABLE {prefix}revisions(
                revision INTEGER PRIMARY KEY AUTOINCREMENT,partition_key TEXT NOT NULL,
                kind INTEGER NOT NULL,record_id TEXT NOT NULL,operation INTEGER NOT NULL,
                recorded_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),payload TEXT NOT NULL);
            """);
        sql.Append($"CREATE INDEX ix_{prefix}revision_record ON {prefix}revisions(partition_key,kind,record_id,revision);");
        if (postgres)
            sql.Append($"""
                CREATE FUNCTION {prefix}revision_immutable() RETURNS trigger LANGUAGE plpgsql AS $history$
                BEGIN RAISE EXCEPTION 'memory_revision_immutable'; END $history$;
                CREATE TRIGGER {prefix}revision_immutable BEFORE UPDATE ON {prefix}revisions
                    FOR EACH ROW EXECUTE FUNCTION {prefix}revision_immutable();
                CREATE FUNCTION {prefix}record_revision() RETURNS trigger LANGUAGE plpgsql AS $history$
                BEGIN
                    IF TG_OP='DELETE' THEN
                        INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload)
                            VALUES(OLD.partition_key,TG_ARGV[0]::integer,OLD.id,3,OLD.payload);
                        RETURN OLD;
                    END IF;
                    IF TG_OP='UPDATE' AND OLD.id<>NEW.id THEN RAISE EXCEPTION 'memory_record_identity_immutable'; END IF;
                    IF TG_OP='UPDATE' AND OLD.payload=NEW.payload AND OLD.partition_key=NEW.partition_key THEN RETURN NEW; END IF;
                    INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload)
                        VALUES(NEW.partition_key,TG_ARGV[0]::integer,NEW.id,CASE WHEN TG_OP='INSERT' THEN 1 ELSE 2 END,NEW.payload);
                    RETURN NEW;
                END $history$;
                """);
        else sql.Append($"CREATE TRIGGER {prefix}revision_immutable BEFORE UPDATE ON {prefix}revisions BEGIN SELECT RAISE(ABORT,'memory_revision_immutable'); END;");
        for (var kind = 0; kind < Tables.Length; kind++)
        {
            var table = prefix + Tables[kind];
            // Canonical migration has already run. Quarantined rows are never promoted
            // into a normal history read; unknown pre-upgrade changes are not invented.
            sql.Append($"INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload) SELECT partition_key,{kind},id,0,payload FROM {table} WHERE partition_key LIKE 'mp2:%' ORDER BY id;");
            if (postgres)
                sql.Append($"CREATE TRIGGER {table}_revision AFTER INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION {prefix}record_revision('{kind}');");
            else
            {
                sql.Append($"CREATE TRIGGER {table}_revision_insert AFTER INSERT ON {table} BEGIN INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload) VALUES(NEW.partition_key,{kind},NEW.id,1,NEW.payload); END;");
                sql.Append($"CREATE TRIGGER {table}_revision_update AFTER UPDATE ON {table} WHEN OLD.payload<>NEW.payload OR OLD.partition_key<>NEW.partition_key OR OLD.id<>NEW.id BEGIN SELECT CASE WHEN OLD.id<>NEW.id THEN RAISE(ABORT,'memory_record_identity_immutable') END; INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload) VALUES(NEW.partition_key,{kind},NEW.id,2,NEW.payload); END;");
                sql.Append($"CREATE TRIGGER {table}_revision_delete AFTER DELETE ON {table} BEGIN INSERT INTO {prefix}revisions(partition_key,kind,record_id,operation,payload) VALUES(OLD.partition_key,{kind},OLD.id,3,OLD.payload); END;");
            }
        }
        sql.Append($"INSERT INTO {prefix}schema_migrations(id) VALUES('{Version}');");
        await using var install = Command(connection, transaction, sql.ToString());
        await install.ExecuteNonQueryAsync(token);
    }

    internal static string ReadSql(bool postgres, MemoryRecordKind kind) => $"""
        SELECT revision,operation,recorded_at,CAST(payload AS text) FROM {Prefix(postgres)}revisions h
        WHERE partition_key=@partition AND kind=@kind AND record_id=@id AND revision>@after
            AND NOT EXISTS(SELECT 1 FROM {Prefix(postgres)}partition_migration_rows q
                WHERE q.table_name='{Prefix(postgres)}{Tables[(int)kind]}' AND q.record_id=CAST(h.record_id AS text) AND q.disposition='Quarantine')
        ORDER BY revision LIMIT @limit
        """;

    internal static void Validate(MemoryRecordKind kind, Guid recordId, long after, int limit)
    {
        if (!Enum.IsDefined(kind) || recordId == Guid.Empty || after < 0 || limit is < 1 or > 200)
            throw new ArgumentException("memory_revision_query_invalid");
    }

    internal static async Task<MemoryRevisionPage> ReadAsync(DbCommand command, MemoryPartition partition,
        MemoryRecordKind kind, Guid id, int limit, CancellationToken token)
    {
        var items = new List<MemoryRevision>();
        var bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (items.Count == limit) return new(items, items[^1].Revision);
            var payload = reader.GetString(3);
            var length = Encoding.UTF8.GetByteCount(payload);
            if (bytes + length > 1_048_576)
            {
                if (items.Count == 0) throw new InvalidOperationException("memory_revision_page_limit");
                return new(items, items[^1].Revision);
            }
            using var json = JsonDocument.Parse(payload);
            // Filter by structured identity as well as the routing key; no flattened-key aliases.
            if (json.RootElement.GetProperty("partition").Deserialize<MemoryPartition>(JsonOptions) != partition ||
                json.RootElement.GetProperty("id").GetGuid() != id) throw new InvalidOperationException("memory_revision_identity_invalid");
            var when = reader.GetValue(2) is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc))
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            items.Add(new(reader.GetInt64(0), kind, id, partition, (MemoryRevisionOperation)reader.GetInt32(1), when, payload));
            bytes += length;
        }
        return new(items, null);
    }
}
