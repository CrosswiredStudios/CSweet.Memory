using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace CSweet.Memory;

public sealed class MemoryEngine : IMemoryEngine
{
    public const string TelemetryName = "CSweet.Memory";
    private static readonly ActivitySource ActivitySource = new(TelemetryName);
    private static readonly Meter Meter = new(TelemetryName);
    private static readonly Counter<long> EpisodesWritten = Meter.CreateCounter<long>("csweet.memory.episodes.written");
    private static readonly Counter<long> RecallCandidates = Meter.CreateCounter<long>("csweet.memory.recall.candidates");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("csweet.memory.operation.duration", "ms");
    private readonly IMemoryStore _store;
    private readonly IMemoryEnrichmentQueue? _enrichmentQueue;
    private readonly IMemoryScopeAuthorizer _authorizer;
    private readonly IMemoryRedactor _redactor;
    private readonly IMemoryQueryEmbedder? _queryEmbedder;
    private readonly AgentMemoryOptions _options;

    public MemoryEngine(
        IMemoryStore store,
        IOptions<AgentMemoryOptions> options,
        IMemoryEnrichmentQueue? enrichmentQueue = null,
        IMemoryScopeAuthorizer? authorizer = null,
        IMemoryRedactor? redactor = null,
        IMemoryQueryEmbedder? queryEmbedder = null)
    {
        _store = store;
        _options = options.Value;
        _enrichmentQueue = enrichmentQueue;
        _authorizer = authorizer ?? new AllowAllMemoryScopeAuthorizer();
        _redactor = redactor ?? new PassthroughMemoryRedactor();
        _queryEmbedder = queryEmbedder;
    }

    public async Task<MemoryEpisode> IngestAsync(MemoryIngestRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("memory.ingest");
        var started = Stopwatch.GetTimestamp();
        activity?.SetTag("memory.scope", request.Scope.ToString());
        activity?.SetTag("memory.store", _store.GetType().Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Partition.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (request.Content.Length > _options.MaximumEpisodeCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(request), $"Episode content exceeds {_options.MaximumEpisodeCharacters} characters.");
        }
        if (!await _authorizer.CanWriteAsync(request.Partition, request.Scope, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot write to this memory scope.");
        }

        var now = DateTimeOffset.UtcNow;
        var episode = new MemoryEpisode(
            Guid.NewGuid(), request.Partition, request.Scope, request.Content, request.ContentType,
            request.Source, ComputeChecksum(request.Content), request.OccurredAt ?? now, now,
            request.IdempotencyKey, request.ExpiresAt, request.LegalHold, request.Metadata);
        var result = await _store.AppendEpisodeAsync(episode, cancellationToken);
        episode = episode with { Id = result.Id };
        if (result.Created && _enrichmentQueue is not null)
        {
            await _enrichmentQueue.EnqueueAsync(episode, cancellationToken);
        }
        EpisodesWritten.Add(result.Created ? 1 : 0, new KeyValuePair<string, object?>("memory.scope", request.Scope.ToString()));
        activity?.SetTag("memory.created", result.Created);
        OperationDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("memory.operation", "ingest"));
        return episode;
    }

    public async Task<MemoryContextPacket> RecallAsync(MemoryRecallRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("memory.recall");
        var started = Stopwatch.GetTimestamp();
        activity?.SetTag("memory.scope", request.Scope.ToString());
        activity?.SetTag("memory.store", _store.GetType().Name);
        if (!await _authorizer.CanReadAsync(request.Partition, request.Scope, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot read this memory scope.");
        }
        var invocationId = request.InvocationId ?? Guid.NewGuid().ToString("N");
        var embedding = _queryEmbedder is null ? null : await _queryEmbedder.EmbedAsync(request.Query, cancellationToken);
        var candidates = await _store.SearchAsync(new MemorySearchRequest(
            request.Partition, request.Scope, request.Query, _options.RetrievalLimit,
            request.AsOf, request.Layers, embedding, IncludePending: _options.IncludePendingClaims), cancellationToken);
        var ranked = ReciprocalRankFusion.Rank(candidates);
        var budget = request.TokenBudget ?? _options.ContextTokenBudget;
        var items = new List<MemoryContextItem>();
        var usedTokens = 0;
        foreach (var candidate in ranked)
        {
            var content = await _redactor.RedactAsync(candidate.Content, candidate.Sensitivity, cancellationToken);
            var tokens = EstimateTokens(content);
            if (tokens > budget - usedTokens) continue;
            var citation = $"memory:{candidate.Id:N}";
            items.Add(new MemoryContextItem(candidate.Id, candidate.Layer, content, citation, candidate.Score, candidate.Trust));
            usedTokens += tokens;
            await _store.RecordUseAsync(new MemoryUse(
                Guid.NewGuid(), request.Partition, invocationId, candidate.Id, candidate.Layer,
                MemoryUseOutcome.Supplied, DateTimeOffset.UtcNow), cancellationToken);
        }
        RecallCandidates.Add(items.Count, new KeyValuePair<string, object?>("memory.scope", request.Scope.ToString()));
        activity?.SetTag("memory.candidates.returned", items.Count);
        activity?.SetTag("memory.context.tokens", usedTokens);
        OperationDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("memory.operation", "recall"));
        return new MemoryContextPacket(invocationId, items, Render(items), usedTokens);
    }

    public async Task<MemoryClaim> CorrectClaimAsync(Guid claimId, string replacementValue, MemorySource source, CancellationToken cancellationToken = default)
    {
        var current = await _store.GetClaimAsync(claimId, cancellationToken)
            ?? throw new KeyNotFoundException($"Memory claim '{claimId}' was not found.");
        if (!await _authorizer.CanWriteAsync(current.Partition, MemoryScope.User, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot correct this memory scope.");
        }
        var episode = await IngestAsync(new MemoryIngestRequest(current.Partition, MemoryScope.User, replacementValue, source), cancellationToken);
        var replacement = current with
        {
            Id = Guid.NewGuid(), EpisodeId = episode.Id, Value = replacementValue,
            Confirmation = MemoryConfirmationState.Confirmed,
            Trust = MemoryTrustTier.ConfirmedUser, ValidFrom = DateTimeOffset.UtcNow,
            ValidTo = null, RecordedAt = DateTimeOffset.UtcNow, SupersedesClaimId = current.Id
        };
        await _store.WriteClaimAsync(replacement, cancellationToken);
        await _store.SupersedeClaimAsync(current.Id, replacement.Id, replacement.ValidFrom, cancellationToken);
        return replacement;
    }

    public Task ConfirmClaimAsync(Guid claimId, bool confirmed, CancellationToken cancellationToken = default) =>
        _store.SetClaimConfirmationAsync(claimId, confirmed ? MemoryConfirmationState.Confirmed : MemoryConfirmationState.Rejected, cancellationToken);

    public Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => _store.ExportAsync(partition, cancellationToken);
    public Task DeleteAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => _store.DeleteScopeAsync(partition, cancellationToken);

    private static string ComputeChecksum(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
    private static string Render(IEnumerable<MemoryContextItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<memory_context trust=\"untrusted\">");
        foreach (var item in items)
        {
            builder.Append("- [").Append(item.Citation).Append("] ")
                .Append(item.Content.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal))
                .AppendLine();
        }
        builder.Append("</memory_context>");
        return builder.ToString();
    }
}
