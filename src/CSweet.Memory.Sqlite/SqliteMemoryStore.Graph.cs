using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    private async Task<IReadOnlyList<MemoryCandidate>> SearchGraphAsync(MemorySearchRequest request,
        MemoryLexicalQuery lexical, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var roots = new List<Guid>();
        var links = new List<MemoryGraphLink>();
        var sql = "SELECT id FROM memory_entities WHERE partition_key=$partition AND id IN (SELECT id FROM memory_entities_fts WHERE content MATCH $query) ORDER BY id LIMIT 32";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$query", lexical.FullText);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) roots.Add(Guid.Parse(reader.GetString(0)));
        }
        if (roots.Count == 0) return [];
        sql = """
            SELECT edge.payload,source.payload,target.payload FROM memory_edges edge
            JOIN memory_entities source ON source.id=edge.from_id AND source.partition_key=edge.partition_key
            JOIN memory_entities target ON target.id=edge.to_id AND target.partition_key=edge.partition_key
            JOIN memory_episodes p ON p.id=edge.episode_id AND p.partition_key=edge.partition_key
            WHERE edge.partition_key=$partition AND csweet_utc_ticks(edge.valid_from)<=$asOf AND (edge.valid_to IS NULL OR csweet_utc_ticks(edge.valid_to)>$asOf)
                AND COALESCE(json_extract(p.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(p.occurred_at)<=$asOf AND (p.expires_at IS NULL OR csweet_utc_ticks(p.expires_at)>$asOf)
            ORDER BY edge.id LIMIT 512
            """;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
            command.Parameters.AddWithValue("$asOf", asOf.UtcTicks);
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
