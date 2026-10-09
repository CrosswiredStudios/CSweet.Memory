using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    // Direct contributor eligibility belongs before LIMIT. Full sealed-transfer and
    // classification resolution still runs after selection; this is not authorization.
    private static string SourceHeadersEligible(string payload, string partition, string instant = "$now") => $"""
        (json_type({payload},'$.sourceEpisodeIds')='array'
         AND json_array_length(json_extract({payload},'$.sourceEpisodeIds'))<={MemoryProvenance.MaximumSourceEpisodes}
         AND NOT EXISTS (
             SELECT 1 FROM json_each({payload},'$.sourceEpisodeIds') refs
             LEFT JOIN memory_episodes upstream ON upstream.id=refs.value AND upstream.partition_key={partition}
             WHERE upstream.id IS NULL OR refs.value='00000000-0000-0000-0000-000000000000'
                OR coalesce(json_extract(upstream.payload,'$.isSuppressed'),0)<>0
                OR csweet_utc_ticks(json_extract(upstream.payload,'$.occurredAt')) IS NULL
                OR csweet_utc_ticks(json_extract(upstream.payload,'$.occurredAt'))>{instant}
                OR (json_extract(upstream.payload,'$.expiresAt') IS NOT NULL AND
                    (csweet_utc_ticks(json_extract(upstream.payload,'$.expiresAt')) IS NULL OR
                     csweet_utc_ticks(json_extract(upstream.payload,'$.expiresAt'))<={instant}))))
        """;

    private static bool HasBoundedLineage(MemoryEntity entity) => entity.SourceEpisodeIds is not null &&
        entity.SourceEpisodeIds.Count <= MemoryProvenance.MaximumSourceEpisodes;

    private static string MergeSourceIds(string previous, string incoming) => $"""
        (SELECT coalesce(json_group_array(value),'[]') FROM
            (SELECT value FROM json_each(coalesce(json_extract({previous},'$.sourceEpisodeIds'),'[]'))
             UNION SELECT value FROM json_each(coalesce(json_extract({incoming},'$.sourceEpisodeIds'),'[]')) ORDER BY value))
        """;

    private async Task<Dictionary<Guid, MemoryEpisode>> LoadLineageSourcesAsync(MemoryPartition partition,
        IEnumerable<Guid> ids, CancellationToken cancellationToken, DateTimeOffset? asOf = null, MemorySearchBudget? budget = null)
    {
        var selected = ids.Distinct().Take(MemoryProvenance.MaximumReadSourceEpisodes + 1).ToArray();
        budget?.Source(selected.Length);
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
                var source = JsonSerializer.Deserialize<MemoryEpisode>(budget is null ? reader.GetString(0) : budget.Payload(reader.GetString(0)), JsonOptions)!;
                if (source.Partition == partition) sources[source.Id] = source;
            }
        }
        return (await ResolveTransferEpisodesAsync(sources.Values.ToArray(), asOf ?? DateTimeOffset.UtcNow, cancellationToken, budget)).ToDictionary(x => x.Id);
    }

    private async Task<MemoryEntity?> ResolveEntityAsync(MemoryEntity? entity, MemoryPartition partition,
        CancellationToken cancellationToken)
    {
        if (entity is null || entity.SourceEpisodeIds is null || entity.SourceEpisodeIds.Count > MemoryProvenance.MaximumSourceEpisodes) return null;
        return MemoryProvenance.ResolveEntity(entity, partition,
            await LoadLineageSourcesAsync(partition, entity.SourceEpisodeIds, cancellationToken), DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ResolveCandidatesAsync(List<MemoryCandidate> candidates,
        MemoryPartition partition, DateTimeOffset asOf, CancellationToken cancellationToken, MemorySearchBudget? budget = null)
    {
        var sources = await LoadLineageSourcesAsync(partition, candidates.SelectMany(x => x.EpisodeIds), cancellationToken, asOf, budget);
        return candidates.Select(candidate => MemoryProvenance.ResolveSensitivity(partition, candidate.Sensitivity,
                candidate.EpisodeIds, sources, asOf, candidate.RetrievalChannel == "graph" ? MemoryGraphTraversal.MaximumCandidateSources : 3 * MemoryProvenance.MaximumSourceEpisodes + 1) is { } sensitivity
            ? candidate with { Sensitivity = sensitivity, RequiredSharedPartitions =
                MemorySharedAudiences.FromSources(candidate.EpisodeIds.Select(id => sources[id])) is { Length: > 0 } required ? required : null } : null).OfType<MemoryCandidate>().ToArray();
    }
}
