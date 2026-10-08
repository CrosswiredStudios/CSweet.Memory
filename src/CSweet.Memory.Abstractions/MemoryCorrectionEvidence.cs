using System.Text.Json.Serialization;

namespace CSweet.Memory;

/// <summary>Immutable evidence identity of a correction contributor in the correction's partition.</summary>
public sealed record MemoryCorrectionSource(Guid EpisodeId, string SourceFingerprint);

/// <summary>Source ancestry, not authority. Only trusted human review orchestration may create it.</summary>
public sealed record MemoryCorrectionEvidence(Guid ReviewOperationId, IReadOnlyList<MemoryCorrectionSource> Sources)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MemoryPartition>? RequiredSharedPartitions { get; init; }
}

/// <summary>Captures current bounded evidence and reserves room for the new correction in its ancestry.</summary>
public interface IMemoryCorrectionEvidenceStore
{
    Task<MemoryCorrectionEvidence> CaptureCorrectionEvidenceAsync(MemoryPartition partition, Guid reviewOperationId,
        IReadOnlyList<Guid> sourceEpisodeIds, CancellationToken cancellationToken = default);
}
