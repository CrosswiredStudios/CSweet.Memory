using Microsoft.Data.Sqlite;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    private static async Task UpgradeCoreSearchIndexAsync(SqliteConnection connection, CancellationToken token)
    {
        // The marker, triggers and populated backfill commit together. SQLite's write
        // transaction serializes competing initializers and old-version writers.
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM memory_schema_migrations WHERE id='indexed-core-search-v1'";
        if (await command.ExecuteScalarAsync(token) is null)
        {
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_blocks_fts USING fts5(id UNINDEXED,content);
                CREATE TRIGGER IF NOT EXISTS memory_blocks_fts_insert AFTER INSERT ON memory_blocks BEGIN
                    INSERT INTO memory_blocks_fts(id,content) VALUES(new.id,new.name || ' ' || coalesce(json_extract(new.payload,'$.content'),''));
                END;
                CREATE TRIGGER IF NOT EXISTS memory_blocks_fts_update AFTER UPDATE ON memory_blocks BEGIN
                    DELETE FROM memory_blocks_fts WHERE id=old.id;
                    INSERT INTO memory_blocks_fts(id,content) VALUES(new.id,new.name || ' ' || coalesce(json_extract(new.payload,'$.content'),''));
                END;
                CREATE TRIGGER IF NOT EXISTS memory_blocks_fts_delete AFTER DELETE ON memory_blocks BEGIN
                    DELETE FROM memory_blocks_fts WHERE id=old.id;
                END;
                DELETE FROM memory_blocks_fts;
                INSERT INTO memory_blocks_fts(id,content) SELECT id,name || ' ' || coalesce(json_extract(payload,'$.content'),'') FROM memory_blocks;
                INSERT INTO memory_schema_migrations(id) VALUES('indexed-core-search-v1');
                """;
            await command.ExecuteNonQueryAsync(token);
        }
        transaction.Commit();
    }
}
