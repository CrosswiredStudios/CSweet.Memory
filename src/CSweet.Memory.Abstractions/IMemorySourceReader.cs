namespace CSweet.Memory;

/// <summary>Bounded, partition-exact provenance reads for trusted policy enforcement.</summary>
/// <remarks>This interface does not grant access. Callers must authorize the partition first.</remarks>
public interface IMemorySourceReader
{
    Task<MemoryEpisode?> GetEpisodeAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default);
    Task<MemoryEntity?> GetEntityAsync(MemoryPartition partition, Guid id, CancellationToken cancellationToken = default);
}
