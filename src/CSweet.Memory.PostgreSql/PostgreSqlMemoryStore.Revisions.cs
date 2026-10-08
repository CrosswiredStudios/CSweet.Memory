namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    public async Task<MemoryRevisionPage> ReadRevisionsAsync(MemoryPartition partition, MemoryRecordKind kind, Guid recordId,
        long afterRevision = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        MemoryRevisionStorage.Validate(kind, recordId, afterRevision, limit);
        await InitializeAsync(cancellationToken);
        await using var command = CreateCommand(MemoryRevisionStorage.ReadSql(true, kind));
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("kind", (int)kind);
        command.Parameters.AddWithValue("id", recordId);
        command.Parameters.AddWithValue("after", afterRevision);
        command.Parameters.AddWithValue("limit", limit + 1);
        return await MemoryRevisionStorage.ReadAsync(command, partition, kind, recordId, limit, cancellationToken);
    }
}
