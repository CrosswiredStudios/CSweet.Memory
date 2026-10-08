namespace CSweet.Memory;

/// <summary>Trusted lifecycle capability; callers must authorize and audit the operation.</summary>
public interface IMemorySuppressionStore
{
    /// <summary>
    /// Permanently suppresses this episode and other episodes with the same source identity
    /// in its partition. Retains evidence and holds. Replay is harmless; scope deletion does
    /// not remove the source tombstone. This is not physical erasure or hold release.
    /// </summary>
    Task SuppressEpisodeAsync(MemoryPartition partition, Guid episodeId, CancellationToken cancellationToken = default);
}
