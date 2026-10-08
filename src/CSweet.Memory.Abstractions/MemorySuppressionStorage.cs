using System.Data.Common;
using System.Text.Json;

namespace CSweet.Memory;

internal static class MemorySuppressionStorage
{
    private const string Version = "source-suppression-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql; return command;
    }

    internal static async Task EnsureAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        var prefix = postgres ? "csweet_memory_" : "memory_";
        async Task<bool> Applied()
        {
            await using var check = Command(connection, transaction, $"SELECT count(*) FROM {prefix}schema_migrations WHERE id='{Version}'");
            return Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0;
        }
        if (await Applied()) return;
        if (postgres)
        {
            await using var guard = Command(connection, transaction,
                "SELECT pg_advisory_xact_lock(728145092); LOCK TABLE csweet_memory_episodes IN SHARE ROW EXCLUSIVE MODE");
            await guard.ExecuteNonQueryAsync(token);
            if (await Applied()) return;
        }
        var schema = postgres ? """
            CREATE TABLE csweet_memory_suppressions(
                partition_key text NOT NULL,source_type text NOT NULL,source_id text NOT NULL,
                episode_id uuid NOT NULL,suppressed_at timestamptz NOT NULL,
                PRIMARY KEY(partition_key,episode_id));
            CREATE INDEX ix_csweet_memory_suppression_source ON csweet_memory_suppressions(partition_key,source_type,source_id);
            CREATE FUNCTION csweet_memory_suppression_guard() RETURNS trigger LANGUAGE plpgsql AS $suppression$
            BEGIN
                IF TG_OP='UPDATE' THEN
                    IF OLD.payload->>'isSuppressed'='true' AND NEW.payload->>'isSuppressed' IS DISTINCT FROM 'true' THEN
                        RAISE EXCEPTION 'memory_suppression_immutable';
                    END IF;
                END IF;
                IF TG_OP='INSERT' OR NEW.payload->>'isSuppressed' IS DISTINCT FROM 'true' THEN
                    IF EXISTS(SELECT 1 FROM csweet_memory_suppressions s WHERE s.partition_key=NEW.partition_key AND
                        (s.episode_id=NEW.id OR (s.source_type=lower(NEW.payload->'source'->>'type') AND s.source_id=NEW.payload->'source'->>'id'))) THEN
                        RAISE EXCEPTION 'memory_source_suppressed';
                    END IF;
                END IF;
                RETURN NEW;
            END $suppression$;
            CREATE TRIGGER csweet_memory_suppression_guard BEFORE INSERT OR UPDATE ON csweet_memory_episodes
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_suppression_guard();
            """ : """
            CREATE TABLE memory_suppressions(
                partition_key TEXT NOT NULL,source_type TEXT NOT NULL,source_id TEXT NOT NULL,
                episode_id TEXT NOT NULL,suppressed_at TEXT NOT NULL,
                PRIMARY KEY(partition_key,episode_id));
            CREATE INDEX ix_memory_suppression_source ON memory_suppressions(partition_key,source_type,source_id);
            CREATE TRIGGER memory_suppression_guard_update BEFORE UPDATE ON memory_episodes
                WHEN json_extract(NEW.payload,'$.isSuppressed') IS NOT 1 AND
                    (json_extract(OLD.payload,'$.isSuppressed')=1 OR
                        EXISTS(SELECT 1 FROM memory_suppressions s WHERE s.partition_key=NEW.partition_key AND
                            (s.episode_id=NEW.id OR (s.source_type=lower(json_extract(NEW.payload,'$.source.type')) AND s.source_id=json_extract(NEW.payload,'$.source.id')))))
                BEGIN SELECT RAISE(ABORT,'memory_suppression_immutable'); END;
            CREATE TRIGGER memory_suppression_guard_insert BEFORE INSERT ON memory_episodes
                WHEN EXISTS(SELECT 1 FROM memory_suppressions s WHERE s.partition_key=NEW.partition_key AND
                    (s.episode_id=NEW.id OR (s.source_type=lower(json_extract(NEW.payload,'$.source.type')) AND s.source_id=json_extract(NEW.payload,'$.source.id'))))
                BEGIN SELECT RAISE(ABORT,'memory_source_suppressed'); END;
            """;
        await using var install = Command(connection, transaction, schema + $"INSERT INTO {prefix}schema_migrations(id) VALUES('{Version}');");
        await install.ExecuteNonQueryAsync(token);
    }

    internal static async Task SuppressAsync(DbConnection connection, DbTransaction transaction, bool postgres,
        MemoryPartition partition, Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw new ArgumentException("A source episode is required.", nameof(id));
        var prefix = postgres ? "csweet_memory_" : "memory_";
        // Serializes both current and legacy inserts against the tombstone and flag update.
        // SQLite callers already hold its write reservation; PostgreSQL callers can enlist
        // this operation with their authorization, durable job exclusion and audit receipt.
        if (postgres)
        {
            // Wait for source row-locking transactions before taking a row update lock.
            // A weaker writer barrier can deadlock with a reader that later inserts an episode.
            await using var guard = Command(connection, transaction, "LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE");
            await guard.ExecuteNonQueryAsync(token);
        }
        await using var read = Command(connection, transaction, $"SELECT CAST(payload AS text) FROM {prefix}episodes WHERE partition_key=@partition AND id=@id");
        Add(read, "partition", partition.StorageKey); Add(read, "id", postgres ? id : id.ToString("D"));
        var payload = (string?)await read.ExecuteScalarAsync(token);
        if (payload is null)
        {
            await using var replay = Command(connection, transaction, $"SELECT count(*) FROM {prefix}suppressions WHERE partition_key=@partition AND episode_id=@id");
            Add(replay, "partition", partition.StorageKey); Add(replay, "id", postgres ? id : id.ToString("D"));
            if (Convert.ToInt64(await replay.ExecuteScalarAsync(token)) != 0) return;
            throw new KeyNotFoundException();
        }
        var episode = JsonSerializer.Deserialize<MemoryEpisode>(payload, Json) ?? throw new InvalidOperationException("memory_source_invalid");
        if (episode.Id != id || episode.Partition != partition || string.IsNullOrWhiteSpace(episode.Source?.Type) || string.IsNullOrWhiteSpace(episode.Source.Id))
            throw new InvalidOperationException("memory_source_invalid");
        var sourceType = postgres ? "lower(payload->'source'->>'type')" : "lower(json_extract(payload,'$.source.type'))";
        var sourceId = postgres ? "payload->'source'->>'id'" : "json_extract(payload,'$.source.id')";
        var suppressed = postgres ? "COALESCE(payload->>'isSuppressed','false')='true'" : "COALESCE(json_extract(payload,'$.isSuppressed'),0)=1";
        await using var write = Command(connection, transaction, $"""
            INSERT INTO {prefix}suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                SELECT partition_key,{sourceType},{sourceId},id,@now FROM {prefix}episodes
                WHERE partition_key=@partition AND {sourceType}=lower(@type) AND {sourceId}=@source
                ON CONFLICT(partition_key,episode_id) DO NOTHING;
            UPDATE {prefix}episodes SET payload={(postgres ? "jsonb_set(payload,'{isSuppressed}','true'::jsonb)" : "json_set(payload,'$.isSuppressed',json('true'))")}
                WHERE partition_key=@partition AND {sourceType}=lower(@type) AND {sourceId}=@source AND NOT ({suppressed});
            """);
        Add(write, "partition", partition.StorageKey); Add(write, "id", postgres ? id : id.ToString("D"));
        Add(write, "now", postgres ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.ToString("O"));
        Add(write, "type", episode.Source.Type); Add(write, "source", episode.Source.Id);
        await write.ExecuteNonQueryAsync(token);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
}
