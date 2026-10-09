using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    private async Task<IReadOnlyList<MemoryCandidate>> SearchGraphAsync(MemorySearchRequest request,
        MemoryLexicalQuery lexical, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var roots = new List<Guid>();
        var links = new List<MemoryGraphLink>();
        var sql = $"SELECT e.id FROM csweet_memory_entities e WHERE e.partition_key=@partition AND e.search_vector @@ websearch_to_tsquery('simple',@query) AND {SourceHeadersEligible("e.payload", "e.partition_key")} ORDER BY e.id LIMIT 32";
        await using (var command = CreateCommand(sql))
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("query", lexical.FullText);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) roots.Add(reader.GetGuid(0));
        }
        if (roots.Count == 0) return [];
        sql = $"""
            SELECT edge.payload::text,source.payload::text,target.payload::text FROM csweet_memory_edges edge
            JOIN csweet_memory_entities source ON source.id=edge.from_id AND source.partition_key=edge.partition_key
            JOIN csweet_memory_entities target ON target.id=edge.to_id AND target.partition_key=edge.partition_key
            JOIN csweet_memory_episodes p ON p.id=edge.episode_id AND p.partition_key=edge.partition_key
            WHERE edge.partition_key=@partition AND {ValidAt("edge.payload", "validFrom", "validTo")}
                AND {SourceHeadersEligible("edge.payload", "edge.partition_key")}
                AND {SourceHeadersEligible("source.payload", "source.partition_key")}
                AND {SourceHeadersEligible("target.payload", "target.partition_key")}
                AND COALESCE(p.payload->>'isSuppressed','false')='false' AND {ValidAt("p.payload", "occurredAt", "expiresAt")}
            ORDER BY edge.id LIMIT 512
            """;
        await using (var command = CreateCommand(sql))
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("asOfTicks", EpochTicks(asOf));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var link = new MemoryGraphLink(JsonSerializer.Deserialize<MemoryEdge>(reader.GetString(0), JsonOptions)!,
                    JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(1), JsonOptions)!,
                    JsonSerializer.Deserialize<MemoryEntity>(reader.GetString(2), JsonOptions)!);
                if (link.HasBoundedSources) links.Add(link);
            }
        }
        var sources = await LoadLineageSourcesAsync(request.Partition, links.SelectMany(x => x.Sources), cancellationToken, asOf);
        return MemoryGraphTraversal.Search(request.Partition, roots, links, sources, asOf, request.Limit);
    }
}
