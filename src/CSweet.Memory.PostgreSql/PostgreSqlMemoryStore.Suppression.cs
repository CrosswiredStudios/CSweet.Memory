namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore : IMemorySuppressionStore
{
    public async Task SuppressEpisodeAsync(MemoryPartition partition, Guid episodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (_transaction is not null)
        {
            await MemorySuppressionStorage.SuppressAsync(_transaction.Connection!, _transaction, true, partition, episodeId, cancellationToken);
            return;
        }
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await MemorySuppressionStorage.SuppressAsync(connection, transaction, true, partition, episodeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
