namespace CSweet.Memory;

public enum MemoryRecordKind { Episode, Entity, Claim, Edge, Block, Procedure, Embedding }
public enum MemoryRevisionOperation { Baseline, Insert, Update, Delete }

/// <summary>A database-recorded snapshot. Revision is a monotonic cursor, not the caller's block revision or event time.</summary>
public sealed record MemoryRevision(long Revision, MemoryRecordKind Kind, Guid RecordId,
    MemoryPartition Partition, MemoryRevisionOperation Operation, DateTimeOffset RecordedAt, string PayloadJson);

public sealed record MemoryRevisionPage(IReadOnlyList<MemoryRevision> Items, long? NextAfterRevision);

/// <summary>
/// Trusted administrative history, which can contain rejected, expired or formerly sensitive content.
/// Callers must authorize history inspection separately from recall. No agent broker operation exposes this interface.
/// Scope deletion removes its history after retention checks; history is not a substitute for a backup or review audit.
/// </summary>
public interface IMemoryRevisionReader
{
    Task<MemoryRevisionPage> ReadRevisionsAsync(MemoryPartition partition, MemoryRecordKind kind, Guid recordId,
        long afterRevision = 0, int limit = 50, CancellationToken cancellationToken = default);
}
