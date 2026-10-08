namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore : IMemoryErasureStore
{
    public async Task<MemoryErasurePreview> PreviewEpisodeErasureAsync(MemoryPartition partition, Guid episodeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        return await MemoryErasureStorage.PreviewAsync(connection, transaction, false, partition, episodeId, cancellationToken);
    }

    public async Task<MemoryErasureResult> EraseEpisodeAsync(MemoryPartition partition, Guid episodeId, string evidenceToken, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await MemoryErasureStorage.EraseAsync(connection, transaction, false, partition, episodeId, evidenceToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
