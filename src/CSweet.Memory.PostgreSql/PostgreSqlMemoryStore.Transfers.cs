namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    public async Task<MemoryCorrectionEvidence> CaptureCorrectionEvidenceAsync(MemoryPartition partition, Guid reviewOperationId,
        IReadOnlyList<Guid> sourceEpisodeIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, DateTimeOffset.UtcNow)
            .CaptureCorrectionAsync(partition, reviewOperationId, sourceEpisodeIds, cancellationToken);
    }
    public async Task<MemoryTransferEvidence> CaptureTransferEvidenceAsync(KnowledgeTransferPackage approvedPackage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, DateTimeOffset.UtcNow)
            .CaptureAsync(approvedPackage, cancellationToken);
    }

    private async Task<IReadOnlyList<MemoryEpisode>> ResolveTransferEpisodesAsync(IReadOnlyList<MemoryEpisode> episodes,
        DateTimeOffset asOf, CancellationToken cancellationToken, MemorySearchBudget? budget = null)
    {
        if (!episodes.Any(x => x.TransferEvidence is not null || x.CorrectionEvidence is not null || x.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true || string.Equals(x.Source.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase))) return episodes;
        await InitializeAsync(cancellationToken);
        var resolver = new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, asOf,
            budget is null ? null : () => budget.Source(), budget is null ? null : budget.Payload);
        var results = new List<MemoryEpisode>(episodes.Count);
        foreach (var episode in episodes) results.Add(await resolver.ResolveAsync(episode, cancellationToken));
        return results;
    }
}
