using System.Text.Json;
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
    private readonly IMemoryNamespaceResolver _namespaceResolver;
    private readonly IMemoryQueryEmbedder? _queryEmbedder;
    private readonly AgentMemoryOptions _options;

    public MemoryEngine(
        IMemoryStore store,
        IOptions<AgentMemoryOptions> options,
        IMemoryEnrichmentQueue? enrichmentQueue = null,
        IMemoryScopeAuthorizer? authorizer = null,
        IMemoryRedactor? redactor = null,
        IMemoryQueryEmbedder? queryEmbedder = null,
        IMemoryNamespaceResolver? namespaceResolver = null)
    {
        _store = store;
        _options = options.Value;
        _enrichmentQueue = enrichmentQueue;
        _authorizer = authorizer ?? new DenyAllMemoryScopeAuthorizer();
        _redactor = redactor ?? new SafeMemoryRedactor();
        _queryEmbedder = queryEmbedder;
        _namespaceResolver = namespaceResolver ?? new PrimaryMemoryNamespaceResolver();
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
        if (!await _authorizer.CanWriteAsync(request.Partition, request.Scope, request.Access, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot write to this memory scope.");
        }

        var now = DateTimeOffset.UtcNow;
        var episode = new MemoryEpisode(
            Guid.NewGuid(), request.Partition, request.Scope, request.Content, request.ContentType,
            request.Source, ComputeChecksum(request.Content), request.OccurredAt ?? now, now,
            request.IdempotencyKey, request.ExpiresAt, request.LegalHold, request.Metadata, request.Sensitivity, request.OperationalReferences) { TransferEvidence = request.TransferEvidence };
        episode = MemorySourceIntegrity.Seal(episode);
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
        if (!await _authorizer.CanReadAsync(request.Partition, request.Scope, request.Access, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot read this memory scope.");
        }
        var invocationId = request.InvocationId ?? Guid.NewGuid().ToString("N");
        var embedding = _queryEmbedder is null ? null : await _queryEmbedder.EmbedAsync(request.Query, cancellationToken);
        var namespaces = await _namespaceResolver.ResolveReadableNamespacesAsync(request.Partition, request.Scope, request.Access, cancellationToken);
        var candidates = new List<MemoryCandidate>();
        var candidatePartitions = new Dictionary<Guid, MemoryPartition>();
        foreach (var memoryNamespace in namespaces.DistinctBy(item => (item.Partition.StorageKey, item.Scope)))
        {
            if (!await _authorizer.CanReadAsync(memoryNamespace.Partition, memoryNamespace.Scope, request.Access, cancellationToken)) continue;
            var found = await _store.SearchAsync(new MemorySearchRequest(
                memoryNamespace.Partition, memoryNamespace.Scope, request.Query, _options.RetrievalLimit,
                request.AsOf, request.Layers, embedding, IncludePending: _options.IncludePendingClaims), cancellationToken);
            foreach (var candidate in found)
            {
                var allowed = true;
                foreach (var partition in candidate.RequiredSharedPartitions ?? [])
                    if (!await _authorizer.CanReadAsync(partition, MemoryScope.Custom, request.Access, cancellationToken)) { allowed = false; break; }
                if (allowed) candidates.Add(candidate);
            }
            foreach (var candidate in found) candidatePartitions.TryAdd(candidate.Id, memoryNamespace.Partition);
        }
        var ranked = ReciprocalRankFusion.Rank(candidates);
        var budget = request.TokenBudget ?? _options.ContextTokenBudget;
        var items = new List<MemoryContextItem>();
        var usedTokens = 0;
        foreach (var candidate in ranked)
        {
            var content = await _redactor.RedactAsync(candidate.Content, candidate.Sensitivity, request.Access, cancellationToken);
            var tokens = EstimateTokens(content);
            if (tokens > budget - usedTokens) continue;
            var citation = $"memory:{candidate.Id:N}";
            items.Add(new MemoryContextItem(candidate.Id, candidate.Layer, content, citation, candidate.Score, candidate.Trust));
            usedTokens += tokens;
            await _store.RecordUseAsync(new MemoryUse(
                Guid.NewGuid(), candidatePartitions.GetValueOrDefault(candidate.Id, request.Partition), invocationId, candidate.Id, candidate.Layer,
                MemoryUseOutcome.Supplied, DateTimeOffset.UtcNow), cancellationToken);
        }
        RecallCandidates.Add(items.Count, new KeyValuePair<string, object?>("memory.scope", request.Scope.ToString()));
        activity?.SetTag("memory.candidates.returned", items.Count);
        activity?.SetTag("memory.context.tokens", usedTokens);
        OperationDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, new KeyValuePair<string, object?>("memory.operation", "recall"));
        return new MemoryContextPacket(invocationId, items, Render(items), usedTokens);
    }

    public async Task<MemoryClaim> CorrectClaimAsync(Guid claimId, string replacementValue, MemorySource source, MemoryAccessContext? access = null, CancellationToken cancellationToken = default)
    {
        var current = await _store.GetClaimAsync(claimId, cancellationToken)
            ?? throw new KeyNotFoundException($"Memory claim '{claimId}' was not found.");
        var scope = InferScope(current.Partition);
        if (!await _authorizer.CanWriteAsync(current.Partition, scope, access, cancellationToken))
        {
            throw new UnauthorizedAccessException("The caller cannot correct this memory scope.");
        }
        var episode = await IngestAsync(new MemoryIngestRequest(current.Partition, scope, replacementValue, source, Access: access), cancellationToken);
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

    public async Task ConfirmClaimAsync(Guid claimId, bool confirmed, MemoryAccessContext? access = null, CancellationToken cancellationToken = default)
    {
        var claim = await _store.GetClaimAsync(claimId, cancellationToken) ?? throw new KeyNotFoundException($"Memory claim '{claimId}' was not found.");
        if (!await _authorizer.CanWriteAsync(claim.Partition, InferScope(claim.Partition), access, cancellationToken)) throw new UnauthorizedAccessException();
        if (_store is IMemorySourceReader reader)
        {
            var ids = claim.SourceEpisodeIds.Prepend(claim.EpisodeId).ToHashSet();
            foreach (var id in new[] { (Guid?)claim.SubjectEntityId, claim.ObjectEntityId }.OfType<Guid>().Distinct())
                if (await reader.GetEntityAsync(claim.Partition, id, cancellationToken) is { } entity)
                    ids.UnionWith(entity.SourceEpisodeIds);
            MemoryProvenance.ValidateSourceEpisodes(ids.ToArray());
            foreach (var id in ids)
            {
                var source = await reader.GetEpisodeAsync(claim.Partition, id, cancellationToken);
                if (source is null) continue;
                if ((source.TransferEvidence is not null || source.CorrectionEvidence is not null || source.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true) &&
                    !MemorySourceIntegrity.IsVerified(source)) throw new UnauthorizedAccessException();
                foreach (var shared in MemorySharedAudiences.FromSources([source]))
                    if (!await _authorizer.CanReadAsync(shared, MemoryScope.Custom, access, cancellationToken)) throw new UnauthorizedAccessException();
            }
        }
        await _store.SetClaimConfirmationAsync(claimId, confirmed ? MemoryConfirmationState.Confirmed : MemoryConfirmationState.Rejected, cancellationToken);
    }

    public async Task<MemoryExport> ExportAsync(MemoryPartition partition, MemoryAccessContext? access = null, CancellationToken cancellationToken = default)
    {
        if (!await _authorizer.CanReadAsync(partition, InferScope(partition), access, cancellationToken)) throw new UnauthorizedAccessException();
        var export = await _store.ExportAsync(partition, cancellationToken);
        var required = MemorySharedAudiences.FromSources(export.Episodes);
        var allowed = new HashSet<MemoryPartition>();
        foreach (var shared in required)
            if (await _authorizer.CanReadAsync(shared, MemoryScope.Custom, access, cancellationToken)) allowed.Add(shared);
        return MemoryAudienceProjection.Create(export, allowed.Contains);
    }

    public async Task DeleteAsync(MemoryPartition partition, MemoryAccessContext? access = null, CancellationToken cancellationToken = default)
    {
        if (!await _authorizer.CanWriteAsync(partition, InferScope(partition), access, cancellationToken)) throw new UnauthorizedAccessException();
        await _store.DeleteScopeAsync(partition, cancellationToken);
    }

    public async Task<KnowledgeTransferPackage> PrepareKnowledgeTransferAsync(PrepareKnowledgeTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceEmployeeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetEmployeeId);
        if (request.SourceEmployeeId == request.TargetEmployeeId) throw new ArgumentException("Knowledge transfer requires different source and target employees.");
        if (request.SourceNamespaces.Count == 0) throw new ArgumentException("At least one source namespace is required.");
        if (!((request.TargetNamespace.Audience == MemoryAudienceType.Employee && request.TargetNamespace.AudienceId == request.TargetEmployeeId) ||
            (request.TargetNamespace.Audience == MemoryAudienceType.UserRelationship && request.TargetNamespace.Partition.AgentId == request.TargetEmployeeId &&
                request.TargetNamespace.Partition.UserId == request.TargetNamespace.AudienceId)))
            throw new ArgumentException("The target namespace must be the replacement employee's namespace.");
        if (request.DebriefSensitivity > request.MaximumSensitivity)
            throw new InvalidOperationException("The debrief exceeds the maximum sensitivity permitted for this transfer.");
        if (request.Access.Principal.TenantId != request.TargetNamespace.Partition.TenantId) throw new UnauthorizedAccessException("Cross-tenant knowledge transfer is forbidden.");
        if (!await _authorizer.CanWriteAsync(request.TargetNamespace.Partition, request.TargetNamespace.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException("The caller cannot create knowledge for the target employee.");

        var items = new List<KnowledgeTransferItem>();
        foreach (var sourceNamespace in request.SourceNamespaces.DistinctBy(item => (item.Partition.StorageKey, item.Scope)))
        {
            if (sourceNamespace.Partition.TenantId != request.Access.Principal.TenantId ||
                !await _authorizer.CanReadAsync(sourceNamespace.Partition, sourceNamespace.Scope, request.Access, cancellationToken))
                throw new UnauthorizedAccessException("The caller cannot offload one or more source namespaces.");
            items.AddRange(ToTransferItems(await ExportAsync(sourceNamespace.Partition, request.Access, cancellationToken), sourceNamespace.Partition));
        }

        var budget = Math.Max(1, request.TokenBudget);
        var used = 0;
        var transferLayers = request.Layers ?? new HashSet<MemoryLayer> { MemoryLayer.Semantic, MemoryLayer.Core, MemoryLayer.Procedural };
        var selected = items
            .Where(item => item.Sensitivity <= request.MaximumSensitivity)
            .Where(item => transferLayers.Contains(item.Layer))
            .Where(item => request.SelectedMemoryIds is null || request.SelectedMemoryIds.Contains(item.MemoryId))
            .DistinctBy(item => item.MemoryId)
            .OrderByDescending(item => item.Kind is MemoryClaimKind.Decision or MemoryClaimKind.Commitment or MemoryClaimKind.Handoff or MemoryClaimKind.OpenQuestion)
            .ThenByDescending(item => item.Trust)
            .Where(item => { var tokens = EstimateTokens(item.Content); if (used + tokens > budget) return false; used += tokens; return true; })
            .ToList();

        var package = new KnowledgeTransferPackage(
            Guid.NewGuid(), request.Access.Principal.TenantId, request.SourceEmployeeId, request.TargetEmployeeId,
            request.SourceNamespaces, request.TargetNamespace, request.Debrief, selected, request.DebriefSensitivity,
            KnowledgeTransferStatus.PendingApproval, DateTimeOffset.UtcNow, request.Access.Principal.EmployeeId);
        await TransferStore.WriteKnowledgeTransferAsync(package, cancellationToken);
        return package;
    }

    public async Task<KnowledgeTransferPackage> ApproveKnowledgeTransferAsync(ApproveKnowledgeTransferRequest request, CancellationToken cancellationToken = default)
    {
        var package = await TransferStore.GetKnowledgeTransferAsync(request.PackageId, cancellationToken) ?? throw new KeyNotFoundException();
        if (package.Status != KnowledgeTransferStatus.PendingApproval) throw new InvalidOperationException("Only pending knowledge transfers can be reviewed.");
        if (!await _authorizer.CanWriteAsync(package.TargetNamespace.Partition, package.TargetNamespace.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        package = package with
        {
            Status = request.Approved ? KnowledgeTransferStatus.Approved : KnowledgeTransferStatus.Rejected,
            ApprovedByEmployeeId = request.Access.Principal.EmployeeId,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovalNotes = request.Notes
        };
        if (request.Approved)
        {
            foreach (var source in package.SourceNamespaces)
                if (!await _authorizer.CanReadAsync(source.Partition, source.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
            package = package with { ApprovedEvidence = await EvidenceStore.CaptureTransferEvidenceAsync(package, cancellationToken) };
            foreach (var shared in package.ApprovedEvidence.RequiredSharedPartitions ?? [])
                if (!await _authorizer.CanReadAsync(shared, MemoryScope.Custom, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        }
        await TransferStore.WriteKnowledgeTransferAsync(package, cancellationToken);
        return package;
    }

    public async Task<KnowledgeTransferPackage> ApplyKnowledgeTransferAsync(ApplyKnowledgeTransferRequest request, CancellationToken cancellationToken = default)
    {
        var package = await TransferStore.GetKnowledgeTransferAsync(request.PackageId, cancellationToken) ?? throw new KeyNotFoundException();
        if (package.Status != KnowledgeTransferStatus.Approved) throw new InvalidOperationException("Knowledge transfer must be approved before it is applied.");
        if (!await _authorizer.CanWriteAsync(package.TargetNamespace.Partition, package.TargetNamespace.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        if (package.ApprovedEvidence is null) throw new InvalidOperationException("memory_transfer_review_required");
        foreach (var shared in package.ApprovedEvidence.RequiredSharedPartitions ?? [])
            if (!await _authorizer.CanReadAsync(shared, MemoryScope.Custom, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        foreach (var source in package.SourceNamespaces)
            if (!await _authorizer.CanReadAsync(source.Partition, source.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        var currentEvidence = await EvidenceStore.CaptureTransferEvidenceAsync(package, cancellationToken);
        if (JsonSerializer.Serialize(currentEvidence) != JsonSerializer.Serialize(package.ApprovedEvidence))
            throw new InvalidOperationException("memory_transfer_evidence_changed");
        var episode = await IngestAsync(new MemoryIngestRequest(
            package.TargetNamespace.Partition, package.TargetNamespace.Scope, MemoryTransferEvidence.RenderContent(package),
            new MemorySource("knowledge-transfer", package.Id.ToString("D"), package.SourceEmployeeId),
            IdempotencyKey: $"knowledge-transfer:{package.Id:D}", LegalHold: package.ApprovedEvidence.LegalHold,
            Metadata: new Dictionary<string, string>
            {
                ["sourceEmployeeId"] = package.SourceEmployeeId,
                ["targetEmployeeId"] = package.TargetEmployeeId,
                ["approvedByEmployeeId"] = package.ApprovedByEmployeeId ?? string.Empty
            }, Access: request.Access,
            Sensitivity: MemoryProvenance.Maximum(package.Items.Select(x => x.Sensitivity)
                .Append(package.DebriefSensitivity).ToArray())) { TransferEvidence = package.ApprovedEvidence }, cancellationToken);
        package = package with { Status = KnowledgeTransferStatus.Applied, AppliedAt = DateTimeOffset.UtcNow, AppliedEpisodeId = episode.Id };
        await TransferStore.WriteKnowledgeTransferAsync(package, cancellationToken);
        return package;
    }

    public async Task RecordFeedbackAsync(MemoryFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _authorizer.CanReadAsync(request.Partition, request.Scope, request.Access, cancellationToken)) throw new UnauthorizedAccessException();
        await _store.RecordUseAsync(new MemoryUse(
            Guid.NewGuid(), request.Partition, request.InvocationId, request.MemoryId, request.Layer,
            request.Outcome, DateTimeOffset.UtcNow), cancellationToken);
    }

    private IMemoryTransferEvidenceStore EvidenceStore => _store as IMemoryTransferEvidenceStore ??
        throw new NotSupportedException("The store cannot validate transfer evidence.");

    private IKnowledgeTransferStore TransferStore => _store as IKnowledgeTransferStore ??
        throw new NotSupportedException($"{_store.GetType().Name} does not support durable knowledge transfer.");

    private static IEnumerable<KnowledgeTransferItem> ToTransferItems(MemoryExport export, MemoryPartition partition) =>
        MemoryReadProjection.TransferItems(MemoryReadProjection.Create(export, partition,
            MemorySensitivity.Restricted, DateTimeOffset.UtcNow), partition);
    private static MemoryScope InferScope(MemoryPartition partition) => partition.ConversationId is not null ? MemoryScope.Conversation
        : partition.UserId is not null ? MemoryScope.User : partition.AgentId is not null ? MemoryScope.Agent
        : partition.ApplicationId is not null ? MemoryScope.Application : MemoryScope.Tenant;

    private static MemoryTrustTier SourceTrust(string sourceType) => sourceType.Equals("application", StringComparison.OrdinalIgnoreCase)
        ? MemoryTrustTier.Authoritative : sourceType.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? MemoryTrustTier.UnconfirmedUser : MemoryTrustTier.External;

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
