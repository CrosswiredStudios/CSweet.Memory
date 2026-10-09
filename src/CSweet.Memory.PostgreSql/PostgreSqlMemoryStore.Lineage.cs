using System.Text.Json;
using NpgsqlTypes;

namespace CSweet.Memory;

public sealed partial class PostgreSqlMemoryStore
{
    // Internal SQL expressions only. Avoid casting malformed legacy source IDs,
    // and keep current sealed-transfer/classification resolution authoritative.
    private static string SourceHeadersEligible(string payload, string partition)
    {
        var ids = $"CASE WHEN jsonb_typeof({payload}->'sourceEpisodeIds')='array' THEN {payload}->'sourceEpisodeIds' ELSE '[]'::jsonb END";
        const string guidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
        return $"""
            (jsonb_typeof({payload}->'sourceEpisodeIds')='array'
             AND jsonb_array_length({ids})<={MemoryProvenance.MaximumSourceEpisodes}
             AND NOT EXISTS (
                 SELECT 1 FROM jsonb_array_elements_text({ids}) refs(id)
                 LEFT JOIN csweet_memory_episodes upstream ON upstream.partition_key={partition}
                    AND upstream.id=CASE WHEN refs.id ~ '{guidPattern}' THEN refs.id::uuid END
                 WHERE upstream.id IS NULL OR refs.id='00000000-0000-0000-0000-000000000000'
                    OR coalesce(upstream.payload->>'isSuppressed','false')<>'false'
                    OR upstream.payload->>'occurredAt' IS NULL
                    OR NOT {ValidAt("upstream.payload", "occurredAt", "expiresAt")}))
            """;
    }

    private static bool HasBoundedLineage(MemoryEntity entity) => entity.SourceEpisodeIds is not null &&
        entity.SourceEpisodeIds.Count <= MemoryProvenance.MaximumSourceEpisodes;

    private static string MergeSourceIds(string previous, string incoming) => $"""
        (SELECT COALESCE(jsonb_agg(v ORDER BY v),'[]'::jsonb) FROM
            (SELECT DISTINCT v FROM jsonb_array_elements(
                COALESCE(NULLIF({previous}->'sourceEpisodeIds','null'::jsonb),'[]'::jsonb) ||
                COALESCE(NULLIF({incoming}->'sourceEpisodeIds','null'::jsonb),'[]'::jsonb)) AS refs(v)) AS merged)
        """;

    private async Task<Dictionary<Guid, MemoryEpisode>> LoadLineageSourcesAsync(MemoryPartition partition,
        IEnumerable<Guid> ids, CancellationToken cancellationToken, DateTimeOffset? asOf = null, MemorySearchBudget? budget = null)
    {
        var selected = ids.Distinct().Take(MemoryProvenance.MaximumReadSourceEpisodes + 1).ToArray();
        budget?.Source(selected.Length);
        var sources = new Dictionary<Guid, MemoryEpisode>();
        if (selected.Length == 0 || selected.Length > MemoryProvenance.MaximumReadSourceEpisodes) return sources;
        await using var command = CreateCommand("SELECT payload::text FROM csweet_memory_episodes WHERE partition_key=@partition AND id=ANY(@ids)");
        command.Parameters.AddWithValue("partition", partition.StorageKey);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, selected);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = JsonSerializer.Deserialize<MemoryEpisode>(budget is null ? reader.GetString(0) : budget.Payload(reader.GetString(0)), JsonOptions)!;
            if (source.Partition == partition) sources[source.Id] = source;
        }
        await reader.DisposeAsync();
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
