namespace CSweet.Memory;

public enum MemoryErasureKind { Episode, Entity, Claim, Edge, Block, Procedure, Embedding, Use, Transfer }

/// <summary>An identity affected by erasure; contains no source content.</summary>
public sealed record MemoryErasureTarget(MemoryErasureKind Kind, Guid Id, MemoryPartition Partition);
public sealed record MemoryErasurePreview(MemoryPartition Partition, Guid EpisodeId, string EvidenceToken,
    IReadOnlyList<MemoryErasureTarget> Targets, string? BlockedReason);
public sealed record MemoryErasureResult(MemoryPartition Partition, Guid EpisodeId, string EvidenceToken,
    int ErasedRecords, int ErasedRevisions, DateTimeOffset ErasedAt, bool WasReplay);

/// <summary>
/// Trusted administrative erasure, not an agent capability or authorization boundary.
/// Hosts must authorize every affected audience and coordinate external jobs, caches,
/// source exclusions and audit in the same transaction before promising user-level forgetting.
/// Database deletion does not remove backups, logs, replicas or previously delivered content.
/// </summary>
public interface IMemoryErasureStore
{
    Task<MemoryErasurePreview> PreviewEpisodeErasureAsync(MemoryPartition partition, Guid episodeId,
        CancellationToken cancellationToken = default);
    Task<MemoryErasureResult> EraseEpisodeAsync(MemoryPartition partition, Guid episodeId, string evidenceToken,
        CancellationToken cancellationToken = default);
}
