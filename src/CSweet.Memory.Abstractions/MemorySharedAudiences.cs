namespace CSweet.Memory;

/// <summary>Canonical shared restrictions carried by transferred source evidence.</summary>
public static class MemorySharedAudiences
{
    public const int MaximumPartitions = 32;

    public static bool IsCanonical(MemoryPartition partition)
    {
        if (partition is null || string.IsNullOrWhiteSpace(partition.TenantId) || string.IsNullOrWhiteSpace(partition.ApplicationId) ||
            partition.AgentId is not null || partition.UserId is not null || partition.ConversationId is not null || partition.CustomNamespace is null ||
            !(partition.CustomNamespace.StartsWith("team:", StringComparison.Ordinal) || partition.CustomNamespace.StartsWith("role:", StringComparison.Ordinal)) ||
            !Guid.TryParseExact(partition.CustomNamespace[5..], "D", out var id) || id == Guid.Empty) return false;
        return partition.CustomNamespace == partition.CustomNamespace[..5] + id.ToString("D");
    }

    public static MemoryPartition[] FromSources(IEnumerable<MemoryEpisode> sources) => Merge(sources.SelectMany(x =>
    {
        var partitions = Required(x);
        if (partitions?.Count > MaximumPartitions) throw new InvalidOperationException("memory_transfer_audience_invalid");
        return partitions ?? [];
    }));

    public static IReadOnlyList<MemoryPartition>? Required(MemoryEpisode source)
    {
        if (source.TransferEvidence is not null && source.CorrectionEvidence is not null)
            throw new InvalidOperationException("memory_evidence_ambiguous");
        return source.CorrectionEvidence?.RequiredSharedPartitions ?? source.TransferEvidence?.RequiredSharedPartitions;
    }

    public static MemoryPartition[] Merge(IEnumerable<MemoryPartition> partitions)
    {
        var result = new HashSet<MemoryPartition>();
        foreach (var partition in partitions)
        {
            if (!IsCanonical(partition)) throw new InvalidOperationException("memory_transfer_audience_invalid");
            result.Add(partition);
            if (result.Count > MaximumPartitions) throw new InvalidOperationException("memory_transfer_audience_invalid");
        }
        return result.OrderBy(x => x.StorageKey, StringComparer.Ordinal).ToArray();
    }
}
