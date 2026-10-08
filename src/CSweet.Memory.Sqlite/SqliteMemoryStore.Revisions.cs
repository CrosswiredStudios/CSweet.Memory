namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    public async Task<MemoryRevisionPage> ReadRevisionsAsync(MemoryPartition partition, MemoryRecordKind kind, Guid recordId,
        long afterRevision = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        MemoryRevisionStorage.Validate(kind, recordId, afterRevision, limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = MemoryRevisionStorage.ReadSql(false, kind);
        command.Parameters.AddWithValue("@partition", partition.StorageKey);
        command.Parameters.AddWithValue("@kind", (int)kind);
        command.Parameters.AddWithValue("@id", recordId.ToString("D"));
        command.Parameters.AddWithValue("@after", afterRevision);
        command.Parameters.AddWithValue("@limit", limit + 1);
        return await MemoryRevisionStorage.ReadAsync(command, partition, kind, recordId, limit, cancellationToken);
    }
}
