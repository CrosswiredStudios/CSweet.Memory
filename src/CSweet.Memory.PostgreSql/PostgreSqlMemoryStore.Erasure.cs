namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore : IMemoryErasureStore
{
    public async Task<MemoryErasurePreview> PreviewEpisodeErasureAsync(MemoryPartition partition, Guid episodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (_transaction is not null)
            return await MemoryErasureStorage.PreviewAsync(_transaction.Connection!, _transaction, true, partition, episodeId, cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        return await MemoryErasureStorage.PreviewAsync(connection, transaction, true, partition, episodeId, cancellationToken);
    }

    public async Task<MemoryErasureResult> EraseEpisodeAsync(MemoryPartition partition, Guid episodeId, string evidenceToken, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (_transaction is not null)
            return await MemoryErasureStorage.EraseAsync(_transaction.Connection!, _transaction, true, partition, episodeId, evidenceToken, cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var result = await MemoryErasureStorage.EraseAsync(connection, transaction, true, partition, episodeId, evidenceToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
