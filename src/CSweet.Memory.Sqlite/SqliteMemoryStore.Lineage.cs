using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    private static bool HasBoundedLineage(MemoryEntity entity) => entity.SourceEpisodeIds is not null &&
        entity.SourceEpisodeIds.Count <= MemoryProvenance.MaximumSourceEpisodes;

    private static string MergeSourceIds(string previous, string incoming) => $"""
        (SELECT coalesce(json_group_array(value),'[]') FROM
            (SELECT value FROM json_each(coalesce(json_extract({previous},'$.sourceEpisodeIds'),'[]'))
             UNION SELECT value FROM json_each(coalesce(json_extract({incoming},'$.sourceEpisodeIds'),'[]')) ORDER BY value))
        """;

    private async Task<Dictionary<Guid, MemoryEpisode>> LoadLineageSourcesAsync(MemoryPartition partition,
        IEnumerable<Guid> ids, CancellationToken cancellationToken, DateTimeOffset? asOf = null)
    {
        var selected = ids.Distinct().Take(MemoryProvenance.MaximumReadSourceEpisodes + 1).ToArray();
        var sources = new Dictionary<Guid, MemoryEpisode>();
        if (selected.Length == 0 || selected.Length > MemoryProvenance.MaximumReadSourceEpisodes) return sources;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in selected.Chunk(256))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT payload FROM memory_episodes WHERE partition_key=$partition AND id IN ({string.Join(',', batch.Select((_, index) => $"$id{index}"))})";
            command.Parameters.AddWithValue("$partition", partition.StorageKey);
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue($"$id{index}", batch[index].ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var source = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), JsonOptions)!;
                if (source.Partition == partition) sources[source.Id] = source;
            }
        }
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
