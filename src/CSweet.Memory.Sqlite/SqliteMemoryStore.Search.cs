using System.Text.Json;

namespace CSweet.Memory;

public sealed partial class SqliteMemoryStore
{
    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var lexical = MemoryLexicalQuery.Parse(request.Query);
        if (lexical.Terms.Count == 0) return [];
        request = request with { Limit = Math.Clamp(request.Limit, 1, 100), AsOf = request.AsOf ?? DateTimeOffset.UtcNow };
        var budget = new MemorySearchBudget();
        var results = new List<MemoryCandidate>();
        foreach (var layer in new[] { MemoryLayer.Core, MemoryLayer.Episodic, MemoryLayer.Semantic, MemoryLayer.Procedural })
        {
            if (!Included(request, layer)) continue;
            var channel = request with { Layers = new HashSet<MemoryLayer> { layer }, Embedding = null };
            results.AddRange(await MemorySearchSelection.SelectAsync(request.Limit,
                (offset, take, token) => SearchPageAsync(channel with { Limit = take }, offset, budget, token),
                (page, token) => ResolveCandidatesAsync(page.ToList(), request.Partition, request.AsOf.Value, token, budget),
                cancellationToken));
        }
        if (Included(request, MemoryLayer.Semantic))
            results.AddRange(await SearchGraphAsync(request, lexical, request.AsOf.Value, cancellationToken, budget));
        if (request.Embedding is { Count: > 0 } && Included(request, MemoryLayer.Episodic))
        {
            var vectors = await MemorySearchSelection.SelectAsync(1024,
                (offset, take, token) => SearchVectorPageAsync(request with { Limit = take }, offset, budget, token),
                (page, token) => ResolveCandidatesAsync(page.ToList(), request.Partition, request.AsOf.Value, token, budget),
                cancellationToken);
            results.AddRange(vectors.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Id).Take(request.Limit));
        }
        // Re-read accepted contributors after selection. The platform also reauthorizes
        // their source revisions and audience immediately before provider delivery.
        return await ResolveCandidatesAsync(results, request.Partition, request.AsOf.Value, cancellationToken, budget);
    }

    private async Task<MemorySearchPage<MemoryCandidate>> SearchVectorPageAsync(MemorySearchRequest request, int offset,
        MemorySearchBudget budget, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding.payload,episode.payload FROM memory_embeddings embedding JOIN memory_episodes episode ON episode.id=embedding.memory_id AND episode.partition_key=embedding.partition_key WHERE embedding.partition_key=$partition AND embedding.layer=1 AND COALESCE(json_extract(episode.payload,'$.isSuppressed'),0)=0 AND csweet_utc_ticks(episode.occurred_at)<=$now AND (episode.expires_at IS NULL OR csweet_utc_ticks(episode.expires_at)>$now) ORDER BY embedding.id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$partition", request.Partition.StorageKey);
        command.Parameters.AddWithValue("$now", request.AsOf!.Value.UtcTicks);
        command.Parameters.AddWithValue("$limit", request.Limit);
        command.Parameters.AddWithValue("$offset", offset);
        var results = new List<MemoryCandidate>();
        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            budget.Candidate();
            rows++;
            var embedding = JsonSerializer.Deserialize<MemoryEmbedding>(budget.Payload(reader.GetString(0)), JsonOptions)!;
            var episode = JsonSerializer.Deserialize<MemoryEpisode>(budget.Payload(reader.GetString(1)), JsonOptions)!;
            if (embedding.Vector.Count != request.Embedding!.Count || embedding.Partition != request.Partition ||
                embedding.Layer != MemoryLayer.Episodic ||
                !MemoryProvenance.IsSnapshotCurrent(episode, request.Partition, embedding.MemoryId, request.AsOf!.Value)) continue;
            results.Add(new MemoryCandidate(episode.Id, MemoryLayer.Episodic, episode.Content,
                CosineSimilarity(request.Embedding, embedding.Vector), SourceTrust(episode.Source.Type),
                MemoryConfirmationState.NotRequired, episode.Sensitivity, episode.OccurredAt, episode.ExpiresAt, [episode.Id], "vector"));
        }
        return new(results, rows);
    }
}
