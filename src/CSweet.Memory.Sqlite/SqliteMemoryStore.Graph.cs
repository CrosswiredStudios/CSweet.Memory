using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    private async Task<IReadOnlyList<MemoryCandidate>> SearchGraphAsync(MemorySearchRequest request,
        MemoryLexicalQuery lexical, DateTimeOffset asOf, CancellationToken cancellationToken, MemorySearchBudget budget)
    {
        await using var connection = await OpenAsync(cancellationToken);
        async Task<MemorySearchPage<MemoryEntity>> ReadRoots(int offset, int take, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""SELECT e.payload FROM memory_entities e WHERE e.partition_key=$partition AND e.id IN (SELECT id FROM memory_entities_fts WHERE content MATCH $query) AND {SourceHeadersEligible("e.payload", "e.partition_key", "$asOf")} ORDER BY e.id LIMIT $limit OFFSET $offset""";
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$asOf", asOf.UtcTicks);
            command.Parameters.AddWithValue("$limit", take);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$query", lexical.FullText);
            var roots = new List<MemoryEntity>();
            var rows = 0;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                budget.Candidate();
                rows++;
                var entity = JsonSerializer.Deserialize<MemoryEntity>(budget.Payload(reader.GetString(0)), JsonOptions)!;
                if (HasBoundedLineage(entity)) roots.Add(entity);
            }
            return new(roots, rows);
        }
        async Task<IReadOnlyList<MemoryEntity>> ValidateRoots(IReadOnlyList<MemoryEntity> roots, CancellationToken token)
        {
            var sources = await LoadLineageSourcesAsync(request.Partition, roots.SelectMany(x => x.SourceEpisodeIds), token, asOf, budget);
            return roots.Select(x => MemoryProvenance.ResolveEntity(x, request.Partition, sources, asOf)).OfType<MemoryEntity>().ToArray();
        }
        var roots = await MemorySearchSelection.SelectAsync(32, ReadRoots, ValidateRoots, cancellationToken);
        if (roots.Count == 0) return [];

        async Task<MemorySearchPage<MemoryGraphLink>> ReadLinks(int offset, int take, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT edge.payload,source.payload,target.payload FROM memory_edges edge
                JOIN memory_entities source ON source.id=edge.from_id AND source.partition_key=edge.partition_key
                JOIN memory_entities target ON target.id=edge.to_id AND target.partition_key=edge.partition_key
                JOIN memory_episodes p ON p.id=edge.episode_id AND p.partition_key=edge.partition_key
                WHERE edge.partition_key=$partition AND csweet_utc_ticks(edge.valid_from)<=$asOf AND (edge.valid_to IS NULL OR csweet_utc_ticks(edge.valid_to)>$asOf)
                  AND {SourceHeadersEligible("edge.payload", "edge.partition_key", "$asOf")}
                  AND {SourceHeadersEligible("source.payload", "source.partition_key", "$asOf")}
                  AND {SourceHeadersEligible("target.payload", "target.partition_key", "$asOf")}
                  AND COALESCE(json_extract(p.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(p.occurred_at)<=$asOf AND (p.expires_at IS NULL OR csweet_utc_ticks(p.expires_at)>$asOf)
                ORDER BY edge.id LIMIT $limit OFFSET $offset
            """;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$asOf", asOf.UtcTicks);
            command.Parameters.AddWithValue("$limit", take);
            command.Parameters.AddWithValue("$offset", offset);
            var links = new List<MemoryGraphLink>();
            var rows = 0;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                budget.Candidate();
                rows++;
                var link = new MemoryGraphLink(
                    JsonSerializer.Deserialize<MemoryEdge>(budget.Payload(reader.GetString(0)), JsonOptions)!,
                    JsonSerializer.Deserialize<MemoryEntity>(budget.Payload(reader.GetString(1)), JsonOptions)!,
                    JsonSerializer.Deserialize<MemoryEntity>(budget.Payload(reader.GetString(2)), JsonOptions)!);
                if (link.HasBoundedSources) links.Add(link);
            }
            return new(links, rows);
        }
        async Task<IReadOnlyList<MemoryGraphLink>> ValidateLinks(IReadOnlyList<MemoryGraphLink> links, CancellationToken token)
        {
            var sources = await LoadLineageSourcesAsync(request.Partition, links.SelectMany(x => x.Sources), token, asOf, budget);
            return links.Where(link =>
            {
                var edge = link.Edge;
                if (edge.Partition != request.Partition || edge.ValidFrom > asOf || edge.ValidTo <= asOf ||
                    !Enum.IsDefined(edge.Trust) || !double.IsFinite(edge.Confidence)) return false;
                var from = MemoryProvenance.ResolveEntity(link.From, request.Partition, sources, asOf);
                var to = MemoryProvenance.ResolveEntity(link.To, request.Partition, sources, asOf);
                sources.TryGetValue(edge.EpisodeId, out var source);
                return MemoryProvenance.ResolveEdge(edge, source, from, to, asOf, sources) is not null;
            }).ToArray();
        }
        var links = await MemorySearchSelection.SelectAsync(512, ReadLinks, ValidateLinks, cancellationToken);
        var sources = await LoadLineageSourcesAsync(request.Partition, links.SelectMany(x => x.Sources), cancellationToken, asOf, budget);
        return MemoryGraphTraversal.Search(request.Partition, roots.Select(x => x.Id), links, sources, asOf, request.Limit);
    }
}
