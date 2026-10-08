namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore : IMemorySuppressionStore
{
    public async Task SuppressEpisodeAsync(MemoryPartition partition, Guid episodeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await MemorySuppressionStorage.SuppressAsync(connection, transaction, false, partition, episodeId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
