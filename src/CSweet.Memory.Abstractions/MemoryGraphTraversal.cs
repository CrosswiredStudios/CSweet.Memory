namespace CSweet.Memory;

internal sealed record MemoryGraphLink(MemoryEdge Edge, MemoryEntity From, MemoryEntity To)
{
    internal bool HasBoundedSources => MemoryProvenance.HasBoundedSources(Edge.SourceEpisodeIds) &&
        MemoryProvenance.HasBoundedSources(From.SourceEpisodeIds) && MemoryProvenance.HasBoundedSources(To.SourceEpisodeIds);
    internal IEnumerable<Guid> Sources => new[] { Edge.EpisodeId }.Concat(Edge.SourceEpisodeIds)
        .Concat(From.SourceEpisodeIds).Concat(To.SourceEpisodeIds);
}

/// <summary>Bounded traversal of validated evidence. Every returned path remains in one exact partition.</summary>
internal static class MemoryGraphTraversal
{
    internal const int MaximumCandidateSources = 3 * (3 * MemoryProvenance.MaximumSourceEpisodes + 1);
    private sealed record Step(Guid To, int Depth, MemoryCandidate Candidate);

    internal static IReadOnlyList<MemoryCandidate> Search(MemoryPartition partition, IEnumerable<Guid> roots,
        IReadOnlyList<MemoryGraphLink> links, IReadOnlyDictionary<Guid, MemoryEpisode> sources, DateTimeOffset asOf, int limit)
    {
        var eligible = new Dictionary<Guid, List<(Guid To, MemoryCandidate Candidate)>>();
        foreach (var link in links.Take(512).OrderBy(x => x.Edge.Id))
        {
            var edge = link.Edge;
            if (!link.HasBoundedSources || edge.Partition != partition || edge.ValidFrom > asOf || edge.ValidTo <= asOf ||
                !Enum.IsDefined(edge.Trust) || !double.IsFinite(edge.Confidence)) continue;
            var from = MemoryProvenance.ResolveEntity(link.From, partition, sources, asOf);
            var to = MemoryProvenance.ResolveEntity(link.To, partition, sources, asOf);
            sources.TryGetValue(edge.EpisodeId, out var source);
            if (MemoryProvenance.ResolveEdge(edge, source, from, to, asOf, sources) is not { } sensitivity) continue;
            var candidate = new MemoryCandidate(edge.Id, MemoryLayer.Semantic,
                $"{from!.CanonicalName} {edge.Relationship} {to!.CanonicalName}", edge.Confidence, edge.Trust,
                MemoryConfirmationState.NotRequired, sensitivity, edge.ValidFrom, edge.ValidTo,
                link.Sources.Distinct().Order().ToArray(), "graph");
            if (!eligible.TryGetValue(edge.FromEntityId, out var outgoing)) eligible[edge.FromEntityId] = outgoing = [];
            outgoing.Add((edge.ToEntityId, candidate));
        }

        var queue = new Queue<Step>();
        var visited = new HashSet<(Guid Entity, int Depth, MemorySensitivity Sensitivity, MemoryTrustTier Trust)>();
        var best = new Dictionary<Guid, Step>();
        void Visit(Guid to, int depth, MemoryCandidate candidate)
        {
            var step = new Step(to, depth, candidate);
            // Retain a concrete least-restricted path, rather than combining unrelated routes.
            if (!best.TryGetValue(candidate.Id, out var previous) || candidate.Sensitivity < previous.Candidate.Sensitivity ||
                (candidate.Sensitivity == previous.Candidate.Sensitivity && depth < previous.Depth)) best[candidate.Id] = step;
            if (depth < 3 && visited.Add((to, depth, candidate.Sensitivity, candidate.Trust))) queue.Enqueue(step);
        }
        foreach (var root in roots.Distinct().Order().Take(32))
            if (eligible.TryGetValue(root, out var outgoing))
                foreach (var (to, candidate) in outgoing) Visit(to, 1, candidate);
        while (queue.TryDequeue(out var path))
        {
            if (!eligible.TryGetValue(path.To, out var outgoing)) continue;
            foreach (var (to, edge) in outgoing)
            {
                var prior = path.Candidate;
                Visit(to, path.Depth + 1, edge with
                {
                    Sensitivity = MemoryProvenance.Maximum(prior.Sensitivity, edge.Sensitivity),
                    Trust = (MemoryTrustTier)Math.Min((int)prior.Trust, (int)edge.Trust),
                    Score = Math.Min(prior.Score, edge.Score),
                    ValidFrom = prior.ValidFrom > edge.ValidFrom ? prior.ValidFrom : edge.ValidFrom,
                    ValidTo = prior.ValidTo is null ? edge.ValidTo : edge.ValidTo is null ? prior.ValidTo :
                        prior.ValidTo < edge.ValidTo ? prior.ValidTo : edge.ValidTo,
                    EpisodeIds = prior.EpisodeIds.Concat(edge.EpisodeIds).Distinct().Order().ToArray()
                });
            }
        }
        return best.Values.Select(x => x.Candidate).OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(limit).ToArray();
    }
}
