namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    public async Task<MemoryTransferEvidence> CaptureTransferEvidenceAsync(KnowledgeTransferPackage approvedPackage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, DateTimeOffset.UtcNow)
            .CaptureAsync(approvedPackage, cancellationToken);
    }

    private async Task<IReadOnlyList<MemoryEpisode>> ResolveTransferEpisodesAsync(IReadOnlyList<MemoryEpisode> episodes,
        DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        if (!episodes.Any(x => x.TransferEvidence is not null || string.Equals(x.Source.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase))) return episodes;
        await InitializeAsync(cancellationToken);
        var resolver = new MemoryTransferEvidenceStorage(sql => CreateCommand(sql), true, asOf);
        var results = new List<MemoryEpisode>(episodes.Count);
        foreach (var episode in episodes) results.Add(await resolver.ResolveAsync(episode, cancellationToken));
        return results;
    }
}
