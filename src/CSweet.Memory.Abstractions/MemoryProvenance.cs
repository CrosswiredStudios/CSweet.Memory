namespace CSweet.Memory;

/// <summary>Shared source isolation and conservative classification rules; not an authorization policy.</summary>
public static class MemoryProvenance
{
    public const int MaximumSourceEpisodes = 128;
    public const int MaximumReadSourceEpisodes = 8192;

    public static bool HasBoundedSources(IReadOnlyList<Guid>? ids) => ids is not null &&
        ids.Count <= MaximumSourceEpisodes && !ids.Contains(Guid.Empty);

    public static void ValidateSourceEpisodes(IReadOnlyList<Guid> ids)
    {
        if (ids is null || ids.Count > MaximumSourceEpisodes || ids.Contains(Guid.Empty))
            throw new ArgumentException("memory_lineage_invalid");
    }

    public static MemorySensitivity? ResolveSensitivity(MemoryPartition partition, MemorySensitivity sensitivity,
        IReadOnlyList<Guid>? ids, IReadOnlyDictionary<Guid, MemoryEpisode> sources, DateTimeOffset asOf,
        int maximumSources = MaximumSourceEpisodes)
    {
        if (!Enum.IsDefined(sensitivity) || ids is null || ids.Count > maximumSources) return null;
        foreach (var id in ids)
        {
            if (id == Guid.Empty || !sources.TryGetValue(id, out var source) || !IsCurrent(source, partition, id, asOf)) return null;
            sensitivity = Maximum(sensitivity, source.Sensitivity);
        }
        return sensitivity;
    }

    public static MemoryEntity? ResolveEntity(MemoryEntity entity, MemoryPartition partition,
        IReadOnlyDictionary<Guid, MemoryEpisode> sources, DateTimeOffset asOf) =>
        entity.Partition == partition && ResolveSensitivity(partition, entity.Sensitivity, entity.SourceEpisodeIds, sources, asOf) is { } sensitivity
            ? entity with { Sensitivity = sensitivity } : null;

    public static MemoryBlock? ResolveBlock(MemoryBlock block, MemoryPartition partition,
        IReadOnlyDictionary<Guid, MemoryEpisode> sources, DateTimeOffset asOf) =>
        block.Partition == partition && block.UpdatedAt <= asOf &&
        ResolveSensitivity(partition, block.Sensitivity, block.SourceEpisodeIds, sources, asOf) is { } sensitivity
            ? block with { Sensitivity = sensitivity } : null;

    public static MemorySensitivity Maximum(params MemorySensitivity[] values) =>
        values.Any(value => !Enum.IsDefined(value)) ? MemorySensitivity.Restricted : values.Max();

    public static bool IsCurrent(MemoryEpisode? source, MemoryPartition partition, Guid id, DateTimeOffset asOf) =>
        IsSnapshotCurrent(source, partition, id, asOf) &&
        ((source!.TransferEvidence is null && source.CorrectionEvidence is null && !string.Equals(source.Source.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase)) || source.TransferEvidenceVerified);

    internal static bool IsSnapshotCurrent(MemoryEpisode? source, MemoryPartition partition, Guid id, DateTimeOffset asOf) =>
        source is not null && !source.IsSuppressed && source.Id == id && source.Partition == partition &&
        MemorySourceIntegrity.IsVerified(source) &&
        Enum.IsDefined(source.Sensitivity) && source.OccurredAt <= asOf &&
        (source.ExpiresAt is null || source.ExpiresAt > asOf);

    public static bool Matches(MemoryEntity? entity, MemoryPartition partition, Guid id) =>
        entity is not null && entity.Id == id && entity.Partition == partition && Enum.IsDefined(entity.Sensitivity);

    public static MemoryClaim? ResolveClaim(MemoryClaim claim, MemoryEpisode? source,
        MemoryEntity? subject, MemoryEntity? objectEntity, DateTimeOffset asOf)
        => ResolveClaim(claim, source, subject, objectEntity, asOf, new Dictionary<Guid, MemoryEpisode>());

    public static MemoryClaim? ResolveClaim(MemoryClaim claim, MemoryEpisode? source,
        MemoryEntity? subject, MemoryEntity? objectEntity, DateTimeOffset asOf,
        IReadOnlyDictionary<Guid, MemoryEpisode> contributors)
    {
        var resolved = ResolveClaimReferences(claim, source, subject, objectEntity, asOf);
        return resolved is not null && ResolveSensitivity(claim.Partition, resolved.Sensitivity,
            claim.SourceEpisodeIds, contributors, asOf) is { } sensitivity ? resolved with { Sensitivity = sensitivity } : null;
    }

    /// <summary>Checks direct references only. Retrieval must also validate every contributor before returning a candidate.</summary>
    public static MemoryClaim? ResolveClaimReferences(MemoryClaim claim, MemoryEpisode? source,
        MemoryEntity? subject, MemoryEntity? objectEntity, DateTimeOffset asOf)
        => ResolveClaimReferences(claim, source, subject, objectEntity, asOf, false);

    internal static MemoryClaim? ResolveClaimReferences(MemoryClaim claim, MemoryEpisode? source,
        MemoryEntity? subject, MemoryEntity? objectEntity, DateTimeOffset asOf, bool snapshotOnly)
    {
        if (!(snapshotOnly ? IsSnapshotCurrent(source, claim.Partition, claim.EpisodeId, asOf) : IsCurrent(source, claim.Partition, claim.EpisodeId, asOf)) ||
            !Matches(subject, claim.Partition, claim.SubjectEntityId) ||
            (claim.ObjectEntityId is { } objectId && !Matches(objectEntity, claim.Partition, objectId))) return null;
        return claim with { Sensitivity = Maximum(claim.Sensitivity, source!.Sensitivity,
            subject!.Sensitivity, objectEntity?.Sensitivity ?? MemorySensitivity.Public) };
    }

    public static MemorySensitivity? ResolveEdge(MemoryEdge edge, MemoryEpisode? source,
        MemoryEntity? from, MemoryEntity? to, DateTimeOffset asOf) =>
        ResolveEdge(edge, source, from, to, asOf, new Dictionary<Guid, MemoryEpisode>());

    public static MemorySensitivity? ResolveEdge(MemoryEdge edge, MemoryEpisode? source,
        MemoryEntity? from, MemoryEntity? to, DateTimeOffset asOf,
        IReadOnlyDictionary<Guid, MemoryEpisode> contributors) =>
        ResolveEdgeReferences(edge, source, from, to, asOf) is { } sensitivity
            ? ResolveSensitivity(edge.Partition, sensitivity, edge.SourceEpisodeIds, contributors, asOf) : null;

    /// <summary>Checks direct references only. Retrieval must also validate every contributor before returning a candidate.</summary>
    public static MemorySensitivity? ResolveEdgeReferences(MemoryEdge edge, MemoryEpisode? source,
        MemoryEntity? from, MemoryEntity? to, DateTimeOffset asOf) =>
        IsCurrent(source, edge.Partition, edge.EpisodeId, asOf) &&
        Matches(from, edge.Partition, edge.FromEntityId) && Matches(to, edge.Partition, edge.ToEntityId)
            ? Maximum(source!.Sensitivity, from!.Sensitivity, to!.Sensitivity) : null;
}
