namespace CSweet.Memory;

public enum MemoryScope { Tenant, Application, Agent, User, Conversation, Custom }
public enum MemoryLayer { Working, Episodic, Semantic, Procedural, Core }
public enum MemoryTrustTier { External, AgentInference, UnconfirmedUser, ConfirmedUser, Authoritative }
public enum MemoryConfirmationState { NotRequired, Pending, Confirmed, Rejected }
public enum MemorySensitivity { Public, Internal, Personal, Confidential, Restricted }
public enum MemoryUseOutcome { Supplied, Cited, Accepted, Corrected, Rejected }

public sealed record MemoryPartition(
    string TenantId,
    string? ApplicationId = null,
    string? AgentId = null,
    string? UserId = null,
    string? ConversationId = null,
    string? CustomNamespace = null)
{
    public string Key => string.Join('/', new[]
    {
        TenantId, ApplicationId, AgentId, UserId, ConversationId, CustomNamespace
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record MemorySource(string Type, string Id, string? Author = null);

public sealed record MemoryEpisode(
    Guid Id,
    MemoryPartition Partition,
    MemoryScope Scope,
    string Content,
    string ContentType,
    MemorySource Source,
    string Checksum,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? IdempotencyKey = null,
    DateTimeOffset? ExpiresAt = null,
    bool LegalHold = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryEntity(
    Guid Id,
    MemoryPartition Partition,
    string Type,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string? ApplicationKey,
    bool IsProtectedType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemoryClaim(
    Guid Id,
    MemoryPartition Partition,
    Guid EpisodeId,
    Guid SubjectEntityId,
    string Predicate,
    Guid? ObjectEntityId,
    string? Value,
    MemoryTrustTier Trust,
    MemoryConfirmationState Confirmation,
    MemorySensitivity Sensitivity,
    double Confidence,
    double Importance,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    Guid? SupersedesClaimId = null,
    string? ExtractorVersion = null);

public sealed record MemoryEdge(
    Guid Id,
    MemoryPartition Partition,
    Guid EpisodeId,
    Guid FromEntityId,
    string Relationship,
    Guid ToEntityId,
    MemoryTrustTier Trust,
    double Confidence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    bool IsLearned,
    DateTimeOffset RecordedAt);

public sealed record MemoryBlock(
    Guid Id,
    MemoryPartition Partition,
    string Name,
    string Content,
    int Revision,
    int MaximumTokens,
    bool IsPinned,
    MemoryTrustTier Trust,
    DateTimeOffset UpdatedAt);

public sealed record ProceduralMemory(
    Guid Id,
    MemoryPartition Partition,
    Guid EpisodeId,
    string Name,
    string Procedure,
    string? Applicability,
    int Version,
    MemoryTrustTier Trust,
    MemoryConfirmationState Confirmation,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt);

public sealed record MemoryUse(
    Guid Id,
    MemoryPartition Partition,
    string InvocationId,
    Guid MemoryId,
    MemoryLayer Layer,
    MemoryUseOutcome Outcome,
    DateTimeOffset RecordedAt);

public sealed record MemoryEmbedding(
    Guid Id,
    MemoryPartition Partition,
    Guid MemoryId,
    MemoryLayer Layer,
    IReadOnlyList<float> Vector,
    string? Model,
    DateTimeOffset RecordedAt);

public sealed record MemoryCandidate(
    Guid Id,
    MemoryLayer Layer,
    string Content,
    double Score,
    MemoryTrustTier Trust,
    MemoryConfirmationState Confirmation,
    MemorySensitivity Sensitivity,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    IReadOnlyList<Guid> EpisodeIds,
    string RetrievalChannel);

public sealed record MemoryContextItem(
    Guid Id,
    MemoryLayer Layer,
    string Content,
    string Citation,
    double Score,
    MemoryTrustTier Trust);

public sealed record MemoryContextPacket(
    string InvocationId,
    IReadOnlyList<MemoryContextItem> Items,
    string RenderedContext,
    int EstimatedTokens,
    bool IsDegraded = false);
