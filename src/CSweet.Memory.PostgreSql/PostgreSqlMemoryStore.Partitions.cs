namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await InitializeSchemaAsync(cancellationToken);
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (await MemoryPartitionMigration.IsAppliedAsync(connection, transaction, true, cancellationToken))
            {
                await MemoryRevisionStorage.EnsureAsync(connection, transaction, true, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _initialized = true;
                return;
            }
            await MemoryPartitionMigration.LockAsync(connection, transaction, true, cancellationToken);
            await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, true, cancellationToken);
            if (!await MemoryPartitionMigration.IsAppliedAsync(connection, transaction, true, cancellationToken))
            {
                var plan = await MemoryPartitionMigration.InspectAsync(connection, transaction, true, cancellationToken);
                if (plan.Rows.Count != 0) throw new InvalidOperationException("memory_partition_migration_required");
                await MemoryPartitionMigration.ApplyAsync(connection, transaction, true, plan.Fingerprint, cancellationToken);
            }
            await MemoryRevisionStorage.EnsureAsync(connection, transaction, true, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<MemoryPartitionMigrationPlan> InspectPartitionMigrationAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null) throw new InvalidOperationException("Use a standalone store for offline migration.");
        await InitializeSchemaAsync(cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, true, cancellationToken);
        var plan = await MemoryPartitionMigration.InspectAsync(connection, transaction, true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan;
    }

    public async Task ApplyPartitionMigrationAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        if (_transaction is not null) throw new InvalidOperationException("Use a standalone store for offline migration.");
        await InitializeSchemaAsync(cancellationToken);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await MemoryPartitionMigration.LockAsync(connection, transaction, true, cancellationToken);
        await MemoryPartitionMigration.EnsureMetadataAsync(connection, transaction, true, cancellationToken);
        await MemoryPartitionMigration.ApplyAsync(connection, transaction, true, expectedFingerprint, cancellationToken);
        await MemoryRevisionStorage.EnsureAsync(connection, transaction, true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _initialized = true;
    }
}
