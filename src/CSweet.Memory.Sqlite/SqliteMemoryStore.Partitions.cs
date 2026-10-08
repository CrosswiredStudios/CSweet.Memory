namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await InitializeSchemaAsync(cancellationToken);
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var connection = await OpenAsync(cancellationToken, initialize: false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, false, cancellationToken);
            if (!await MemoryPartitionMigration.IsAppliedAsync(connection, transaction, false, cancellationToken))
            {
                var plan = await MemoryPartitionMigration.InspectAsync(connection, transaction, false, cancellationToken);
                if (plan.Rows.Count != 0) throw new InvalidOperationException("memory_partition_migration_required");
                await MemoryPartitionMigration.ApplyAsync(connection, transaction, false, plan.Fingerprint, cancellationToken);
            }
            await MemoryRevisionStorage.EnsureAsync(connection, transaction, false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<MemoryPartitionMigrationPlan> InspectPartitionMigrationAsync(CancellationToken cancellationToken = default)
    {
        await InitializeSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken, initialize: false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, false, cancellationToken);
        var plan = await MemoryPartitionMigration.InspectAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan;
    }

    public async Task ApplyPartitionMigrationAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        await InitializeSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken, initialize: false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, false, cancellationToken);
        await MemoryPartitionMigration.ApplyAsync(connection, transaction, false, expectedFingerprint, cancellationToken);
        await MemoryRevisionStorage.EnsureAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _initialized = true;
    }
}
