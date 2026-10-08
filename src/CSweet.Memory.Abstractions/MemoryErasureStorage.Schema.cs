using System.Data.Common;

namespace CSweet.Memory;

internal static partial class MemoryErasureStorage
{
    private const string Version = "source-erasure-v1";
    internal static async Task EnsureAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
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
            await Execute(connection, transaction, "SELECT pg_advisory_xact_lock(728145092)", token);
            if (await Applied()) return;
            await Execute(connection, transaction, "LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE", token);
            await Execute(connection, transaction, "LOCK TABLE " + string.Join(',', Tables.Skip(1).Select(x => prefix + x)) +
                ",csweet_memory_revisions IN SHARE ROW EXCLUSIVE MODE", token);
        }
        var idType = postgres ? "uuid" : "TEXT";
        var payloadType = postgres ? "jsonb" : "TEXT";
        await Execute(connection, transaction, $"""
            CREATE TABLE {prefix}erased_records(partition_key TEXT NOT NULL,kind INTEGER NOT NULL,record_id {idType} NOT NULL,
                tenant_id TEXT NOT NULL,application_id TEXT,source_type TEXT,source_id TEXT,
                PRIMARY KEY(partition_key,kind,record_id));
            CREATE INDEX ix_{prefix}erased_identity ON {prefix}erased_records(tenant_id,record_id);
            CREATE INDEX ix_{prefix}erased_source ON {prefix}erased_records(tenant_id,application_id,source_type,source_id);
            CREATE TABLE {prefix}erased_sources(partition_key TEXT NOT NULL,tenant_id TEXT NOT NULL,application_id TEXT,
                source_type TEXT NOT NULL,source_id TEXT NOT NULL,PRIMARY KEY(partition_key,source_type,source_id));
            CREATE INDEX ix_{prefix}erased_sources_identity ON {prefix}erased_sources(tenant_id,application_id,source_type,source_id);
            CREATE TABLE {prefix}erasure_receipts(partition_key TEXT NOT NULL,episode_id {idType} NOT NULL,payload {payloadType} NOT NULL,
                PRIMARY KEY(partition_key,episode_id));
            """, token);
        for (var kind = 0; kind < Tables.Length; kind++)
        {
            var table = prefix + Tables[kind];
            // A reference anywhere in the structured payload to an erased identity must
            // not resurrect it under a fresh record ID (including transfer/correction links).
            // Matching is conservative and tenant-local; this never grants read access.
            var tenant = postgres ? (kind == 8 ? "NEW.tenant_id" : "NEW.payload->'partition'->>'tenantId'")
                : (kind == 8 ? "NEW.tenant_id" : "json_extract(NEW.payload,'$.partition.tenantId')");
            var sourceMatch = kind == 0 ? (postgres ? $"""
                OR EXISTS(SELECT 1 FROM {prefix}erased_sources e WHERE e.tenant_id={tenant}
                    AND e.application_id IS NOT DISTINCT FROM NEW.payload->'partition'->>'applicationId'
                    AND e.source_type=lower(NEW.payload->'source'->>'type') AND e.source_id=NEW.payload->'source'->>'id')
                """ : $"""
                OR EXISTS(SELECT 1 FROM {prefix}erased_sources e WHERE e.tenant_id={tenant}
                    AND e.application_id IS json_extract(NEW.payload,'$.partition.applicationId')
                    AND e.source_type=lower(json_extract(NEW.payload,'$.source.type')) AND e.source_id=json_extract(NEW.payload,'$.source.id'))
                """) : "";
            var predicate = postgres ? $"""
                EXISTS(SELECT 1 FROM {prefix}erased_records e WHERE e.tenant_id={tenant} AND
                    (e.record_id=NEW.id OR EXISTS(SELECT 1 FROM jsonb_path_query(NEW.payload,'strict $.**') v
                        WHERE jsonb_typeof(v)='string' AND lower(v #>> ARRAY[]::text[])=e.record_id::text))) {sourceMatch}
                """ : $"""
                EXISTS(SELECT 1 FROM {prefix}erased_records e WHERE e.tenant_id={tenant} AND
                    (e.record_id=NEW.id OR EXISTS(SELECT 1 FROM json_tree(NEW.payload) v
                        WHERE v.type='text' AND lower(v.atom)=e.record_id))) {sourceMatch}
                """;
            if (postgres)
                await Execute(connection, transaction, $"""
                    CREATE FUNCTION {table}_erasure_guard() RETURNS trigger LANGUAGE plpgsql AS $erase$
                    BEGIN
                        IF {predicate} THEN RAISE EXCEPTION 'memory_source_erased'; END IF;
                        RETURN NEW;
                    END $erase$;
                    CREATE TRIGGER {table}_erasure_guard BEFORE INSERT OR UPDATE ON {table}
                        FOR EACH ROW EXECUTE FUNCTION {table}_erasure_guard();
                    """, token);
            else
                foreach (var operation in new[] { "INSERT", "UPDATE" })
                    await Execute(connection, transaction, $"""
                        CREATE TRIGGER {table}_erasure_guard_{operation.ToLowerInvariant()} BEFORE {operation} ON {table}
                            WHEN {predicate} BEGIN SELECT RAISE(ABORT,'memory_source_erased'); END;
                        """, token);
        }
        await Execute(connection, transaction, $"INSERT INTO {prefix}schema_migrations(id) VALUES('{Version}')", token);
    }

    private static async Task BarrierAsync(DbConnection connection, DbTransaction transaction, bool postgres, CancellationToken token)
    {
        if (!postgres) return; // SQLite callers reserve its writer before entry.
        await Execute(connection, transaction, "LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE", token);
        await Execute(connection, transaction, "LOCK TABLE " + string.Join(',', Tables.Skip(1).Select(x => Prefix(true) + x)) +
            ",csweet_memory_revisions,csweet_memory_erased_records,csweet_memory_erased_sources,csweet_memory_erasure_receipts IN SHARE ROW EXCLUSIVE MODE", token);
    }

    private static async Task Execute(DbConnection connection, DbTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = Command(connection, transaction, sql); await command.ExecuteNonQueryAsync(token);
    }
}
