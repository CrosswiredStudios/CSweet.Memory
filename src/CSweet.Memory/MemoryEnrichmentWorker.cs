using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CSweet.Memory;

public sealed class MemoryEnrichmentWorker : BackgroundService, IMemoryEnrichmentQueue
{
    private readonly Channel<MemoryEpisode> _channel = Channel.CreateBounded<MemoryEpisode>(new BoundedChannelOptions(1_024)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly IMemoryStore _store;
    private readonly IMemoryEnricher? _enricher;
    private readonly AgentMemoryOptions _options;

    public MemoryEnrichmentWorker(IMemoryStore store, IOptions<AgentMemoryOptions> options, IMemoryEnricher? enricher = null)
    {
        _store = store;
        _enricher = enricher;
        _options = options.Value;
    }

    public ValueTask EnqueueAsync(MemoryEpisode episode, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(episode, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var episode in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            if (_enricher is null) continue;
            try
            {
                await ProcessAsync(episode, await _enricher.EnrichAsync(episode, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The immutable episode remains available for deterministic search and later re-enrichment.
            }
        }
    }

    private async Task ProcessAsync(MemoryEpisode episode, MemoryEnrichment enrichment, CancellationToken cancellationToken)
    {
        if (enrichment.Embedding is { Count: > 0 })
        {
            await _store.WriteEmbeddingAsync(new MemoryEmbedding(
                Guid.NewGuid(), episode.Partition, episode.Id, MemoryLayer.Episodic,
                enrichment.Embedding, _enricher!.Version, DateTimeOffset.UtcNow), cancellationToken);
        }
        var entities = new Dictionary<string, MemoryEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var extracted in enrichment.Entities)
        {
            var existing = await _store.FindEntityAsync(episode.Partition, extracted.Name, cancellationToken);
            var isProtected = _options.ProtectedEntityTypes.Contains(extracted.Type);
            var type = isProtected || extracted.Type.StartsWith("learned:", StringComparison.OrdinalIgnoreCase)
                ? extracted.Type
                : $"learned:{extracted.Type}";
            var entity = existing ?? new MemoryEntity(
                Guid.NewGuid(), episode.Partition, type, extracted.Name,
                extracted.Aliases ?? [], extracted.ApplicationKey, isProtected,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await _store.UpsertEntityAsync(entity, cancellationToken);
            entities[extracted.Name] = entity;
        }

        foreach (var extracted in enrichment.Claims)
        {
            if (!entities.TryGetValue(extracted.SubjectName, out var subject)) continue;
            entities.TryGetValue(extracted.ObjectName ?? string.Empty, out var objectEntity);
            var confirmation = extracted.Sensitivity >= MemorySensitivity.Confidential
                ? MemoryConfirmationState.Pending
                : MemoryConfirmationState.NotRequired;
            var claim = new MemoryClaim(
                Guid.NewGuid(), episode.Partition, episode.Id, subject.Id, extracted.Predicate,
                objectEntity?.Id, extracted.Value, MemoryTrustTier.AgentInference, confirmation,
                extracted.Sensitivity, Math.Clamp(extracted.Confidence, 0, 1), Math.Clamp(extracted.Importance, 0, 1),
                episode.OccurredAt, null, DateTimeOffset.UtcNow, ExtractorVersion: _enricher!.Version);
            var conflicting = (await _store.ListClaimsAsync(episode.Partition, cancellationToken))
                .FirstOrDefault(existing => existing.SubjectEntityId == subject.Id &&
                    string.Equals(existing.Predicate, extracted.Predicate, StringComparison.OrdinalIgnoreCase) &&
                    existing.ValidTo is null &&
                    !string.Equals(existing.Value, claim.Value, StringComparison.OrdinalIgnoreCase));
            if (conflicting is not null)
            {
                claim = claim with { SupersedesClaimId = conflicting.Id };
            }
            await _store.WriteClaimAsync(claim, cancellationToken);
            if (conflicting is not null)
            {
                await _store.SupersedeClaimAsync(conflicting.Id, claim.Id, claim.ValidFrom, cancellationToken);
            }
        }

        foreach (var extracted in enrichment.Edges)
        {
            if (!entities.TryGetValue(extracted.FromName, out var from) || !entities.TryGetValue(extracted.ToName, out var to)) continue;
            var isProtected = _options.ProtectedRelationships.Contains(extracted.Relationship);
            var relationship = isProtected || extracted.Relationship.StartsWith("learned:", StringComparison.OrdinalIgnoreCase)
                ? extracted.Relationship
                : $"learned:{extracted.Relationship}";
            await _store.WriteEdgeAsync(new MemoryEdge(
                Guid.NewGuid(), episode.Partition, episode.Id, from.Id, relationship, to.Id,
                MemoryTrustTier.AgentInference, Math.Clamp(extracted.Confidence, 0, 1),
                episode.OccurredAt, null, !isProtected, DateTimeOffset.UtcNow), cancellationToken);
        }

        foreach (var extracted in enrichment.Procedures)
        {
            await _store.WriteProcedureAsync(new ProceduralMemory(
                Guid.NewGuid(), episode.Partition, episode.Id, extracted.Name, extracted.Procedure,
                extracted.Applicability, 1, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending,
                episode.OccurredAt, null, DateTimeOffset.UtcNow), cancellationToken);
        }
    }
}
