using System.Text.Json;
using NpgsqlTypes;

namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    private static bool HasBoundedLineage(MemoryEntity entity) => entity.SourceEpisodeIds is not null &&
        entity.SourceEpisodeIds.Count <= MemoryProvenance.MaximumSourceEpisodes;

    private static string MergeSourceIds(string previous, string incoming) => $"""
        (SELECT COALESCE(jsonb_agg(v ORDER BY v),'[]'::jsonb) FROM
            (SELECT DISTINCT v FROM jsonb_array_elements(
                COALESCE(NULLIF({previous}->'sourceEpisodeIds','null'::jsonb),'[]'::jsonb) ||
                COALESCE(NULLIF({incoming}->'sourceEpisodeIds','null'::jsonb),'[]'::jsonb)) AS refs(v)) AS merged)
        """;

    private async Task<Dictionary<Guid, MemoryEpisode>> LoadLineageSourcesAsync(MemoryPartition partition,
        IEnumerable<Guid> ids, CancellationToken cancellationToken, DateTimeOffset? asOf = null)
    {
        var selected = ids.Distinct().Take(MemoryProvenance.MaximumReadSourceEpisodes + 1).ToArray();
        var sources = new Dictionary<Guid, MemoryEpisode>();
        if (selected.Length == 0 || selected.Length > MemoryProvenance.MaximumReadSourceEpisodes) return sources;
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_episodes WHERE partition_key=@partition AND id=ANY(@ids)");
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, selected);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), JsonOptions)!;
            if (source.Partition == partition) sources[source.Id] = source;
        }
        await reader.DisposeAsync();
        return (await ResolveTransferEpisodesAsync(sources.Values.ToArray(), asOf ?? DateTimeOffset.UtcNow, cancellationToken)).ToDictionary(x => x.Id);
    }

    private async Task<MemoryEntity?> ResolveEntityAsync(MemoryEntity? entity, MemoryPartition partition,
        CancellationToken cancellationToken)
    {
        if (entity is null || entity.SourceEpisodeIds is null || entity.SourceEpisodeIds.Count > MemoryProvenance.MaximumSourceEpisodes) return null;
        return MemoryProvenance.ResolveEntity(entity, partition,
            await LoadLineageSourcesAsync(partition, entity.SourceEpisodeIds, cancellationToken), DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ResolveCandidatesAsync(List<MemoryCandidate> candidates,
        MemoryPartition partition, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var sources = await LoadLineageSourcesAsync(partition, candidates.SelectMany(x => x.EpisodeIds), cancellationToken, asOf);
        return candidates.Select(candidate => MemoryProvenance.ResolveSensitivity(partition, candidate.Sensitivity,
                candidate.EpisodeIds, sources, asOf, candidate.RetrievalChannel == "graph" ? MemoryGraphTraversal.MaximumCandidateSources : 3 * MemoryProvenance.MaximumSourceEpisodes + 1) is { } sensitivity
            ? candidate with { Sensitivity = sensitivity } : null).OfType<MemoryCandidate>().ToArray();
    }
}
