using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    private async Task<IReadOnlyList<MemoryCandidate>> SearchGraphAsync(MemorySearchRequest request,
        MemoryLexicalQuery lexical, DateTimeOffset asOf, CancellationToken cancellationToken, MemorySearchBudget budget)
    {
        
        async Task<MemorySearchPage<MemoryEntity>> ReadRoots(int offset, int take, CancellationToken token)
        {
            await using var command = CreateCommand($"""SELECT e.payload::text FROM csweet_memory_entities e WHERE e.partition_key=@partition AND e.search_vector @@ websearch_to_tsquery('simple',@query) AND {SourceHeadersEligible("e.payload", "e.partition_key")} ORDER BY e.id LIMIT @limit OFFSET @offset""");
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            command.Parameters.AddWithValue("limit", take);
            command.Parameters.AddWithValue("offset", offset);
            command.Parameters.AddWithValue("query", lexical.FullText);
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
            await using var command = CreateCommand($"""
                SELECT edge.payload::text,source.payload::text,target.payload::text FROM csweet_memory_edges edge
                JOIN csweet_memory_entities source ON source.id=edge.from_id AND source.partition_key=edge.partition_key
                JOIN csweet_memory_entities target ON target.id=edge.to_id AND target.partition_key=edge.partition_key
                JOIN csweet_memory_episodes p ON p.id=edge.episode_id AND p.partition_key=edge.partition_key
                WHERE edge.partition_key=@partition AND {ValidAt("edge.payload", "validFrom", "validTo")}
                  AND {SourceHeadersEligible("edge.payload", "edge.partition_key")}
                  AND {SourceHeadersEligible("source.payload", "source.partition_key")}
                  AND {SourceHeadersEligible("target.payload", "target.partition_key")}
                  AND COALESCE(p.payload->>'isSuppressed','false')='false' AND {ValidAt("p.payload", "occurredAt", "expiresAt")}
                ORDER BY edge.id LIMIT @limit OFFSET @offset
            """);
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            command.Parameters.AddWithValue("limit", take);
            command.Parameters.AddWithValue("offset", offset);
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
