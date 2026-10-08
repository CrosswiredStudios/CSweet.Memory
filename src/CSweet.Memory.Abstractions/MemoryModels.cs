namespace CSweet.Memory;

public enum MemoryScope { Tenant, Application, Agent, User, Conversation, Custom }
public enum MemoryLayer { Working, Episodic, Semantic, Procedural, Core }
public enum MemoryTrustTier { External, AgentInference, UnconfirmedUser, ConfirmedUser, Authoritative }
public enum MemoryConfirmationState { NotRequired, Pending, Confirmed, Rejected }
public enum MemorySensitivity { Public, Internal, Personal, Confidential, Restricted }
public enum MemoryUseOutcome { Supplied, Cited, Accepted, Corrected, Rejected }
public enum MemoryClaimKind { Fact, Observation, Decision, Commitment, WorkOutcome, Handoff, Feedback, Failure, SkillEvidence, OpenQuestion }
public enum MemoryAudienceType { Organization, Team, Role, Employee, UserRelationship, Case, Conversation, Custom }
public enum KnowledgeTransferStatus { PendingApproval, Approved, Rejected, Applied }

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

    /// <summary>Versioned storage identity over all six fields. Key remains a legacy display/wire value.</summary>
    public string StorageKey
    {
        get
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            hash.AppendData("CSweet.Memory.Partition/v2\0"u8);
            var encoding = new System.Text.UTF8Encoding(false, true);
            Span<byte> length = stackalloc byte[4];
            foreach (var component in new[] { TenantId, ApplicationId, AgentId, UserId, ConversationId, CustomNamespace })
            {
                var bytes = component is null ? null : encoding.GetBytes(component);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
                hash.AppendData(length);
                if (bytes is not null) hash.AppendData(bytes);
            }
            return "mp2:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }
}

public sealed record MemorySource(string Type, string Id, string? Author = null);

public sealed record MemoryPrincipal(
    string TenantId,
    string EmployeeId,
    string? AgentDefinitionId = null,
    string? InstallationId = null,
    IReadOnlySet<string>? RoleIds = null,
    IReadOnlySet<string>? TeamIds = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record MemoryWorkContext(
    string? ObjectiveId = null,
    string? TaskId = null,
    string? CaseId = null,
    string? RoleId = null,
    string? TeamId = null);

/// <summary>A reference to authoritative employee identity; the operational employee record remains outside memory.</summary>
public sealed record MemoryEmployeeIdentity(
    string EmployeeId,
    string AgentDefinitionId,
    string InstallationId,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo = null);

/// <summary>A temporal projection of an authoritative role assignment used for memory access and retrieval.</summary>
public sealed record MemoryRoleAssignment(
    string EmployeeId,
    string RoleId,
    string? TeamId,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo = null);

public sealed record MemoryOperationalReference(string Type, string Id, string? Version = null);

public sealed record MemoryAccessContext(
    MemoryPrincipal Principal,
    string Purpose,
    string Operation,
    MemoryWorkContext? WorkContext = null);

public sealed record MemoryNamespace(
    MemoryPartition Partition,
    MemoryScope Scope,
    MemoryAudienceType Audience,
    string AudienceId);

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
    IReadOnlyDictionary<string, string>? Metadata = null,
    MemorySensitivity Sensitivity = MemorySensitivity.Internal,
    IReadOnlyList<MemoryOperationalReference>? OperationalReferences = null)
{
    /// <summary>Server-owned fingerprint of immutable evidence fields. Missing legacy fingerprints require review.</summary>
    public string? SourceFingerprint { get; init; }
    /// <summary>Current lifecycle policy; suppression also applies to historical valid-time reads.</summary>
    public bool IsSuppressed { get; init; }
    public MemoryTransferEvidence? TransferEvidence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MemoryCorrectionEvidence? CorrectionEvidence { get; init; }
    // Derived, never serialized. Each result is bound to the exact fingerprint it verified, so any
    // evidence mutation or reseal invalidates it until a store resolves the episode again.
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? VerifiedEvidenceFingerprint { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? RetainedEvidenceFingerprint { get; init; }
    /// <summary>Current recall eligibility of transfer/correction ancestry, as resolved by a store.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool TransferEvidenceVerified => VerifiedEvidenceFingerprint is { } verified && verified == SourceFingerprint;
    /// <summary>Retained certificate integrity, independent of suppression, expiry or revocation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool RetainedEvidenceVerified => TransferEvidenceVerified ||
        RetainedEvidenceFingerprint is { } retained && retained == SourceFingerprint;
}

public sealed record MemoryEntity(
    Guid Id,
    MemoryPartition Partition,
    string Type,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    string? ApplicationKey,
    bool IsProtectedType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // Legacy entities have no classification. Only a trusted producer may classify them.
    public MemorySensitivity Sensitivity { get; init; } = MemorySensitivity.Restricted;
    /// <summary>Contributing episodes in this exact partition. Ordinary updates cannot remove prior contributions.</summary>
    public IReadOnlyList<Guid> SourceEpisodeIds { get; init; } = [];
}

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
    string? ExtractorVersion = null,
    MemoryClaimKind Kind = MemoryClaimKind.Fact)
{
    /// <summary>Additional contributing episodes in this exact partition; EpisodeId remains required.</summary>
    public IReadOnlyList<Guid> SourceEpisodeIds { get; init; } = [];
}

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
    DateTimeOffset RecordedAt)
{
    /// <summary>Additional contributing episodes in this exact partition; EpisodeId remains required.</summary>
    public IReadOnlyList<Guid> SourceEpisodeIds { get; init; } = [];
}

public sealed record MemoryBlock(
    Guid Id,
    MemoryPartition Partition,
    string Name,
    string Content,
    int Revision,
    int MaximumTokens,
    bool IsPinned,
    MemoryTrustTier Trust,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Explicit human review state. Missing legacy values retain NotRequired; source policy still applies.</summary>
    public MemoryConfirmationState Confirmation { get; init; } = MemoryConfirmationState.NotRequired;
    public MemorySensitivity Sensitivity { get; init; } = MemorySensitivity.Restricted;
    /// <summary>Contributing episodes in this exact partition. Ordinary updates cannot remove prior contributions.</summary>
    public IReadOnlyList<Guid> SourceEpisodeIds { get; init; } = [];
}

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
    DateTimeOffset RecordedAt)
{
    /// <summary>Additional contributing episodes in this exact partition; EpisodeId remains required.</summary>
    public IReadOnlyList<Guid> SourceEpisodeIds { get; init; } = [];
}

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
    string RetrievalChannel)
{
    /// <summary>Current membership must be checked before this candidate is supplied.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MemoryPartition>? RequiredSharedPartitions { get; init; }
}

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

public sealed record KnowledgeTransferItem(
    Guid MemoryId,
    MemoryPartition SourcePartition,
    MemoryLayer Layer,
    MemoryClaimKind Kind,
    string Content,
    MemorySensitivity Sensitivity,
    MemoryTrustTier Trust,
    IReadOnlyList<Guid> EpisodeIds,
    string Citation);

public sealed record KnowledgeTransferPackage(
    Guid Id,
    string TenantId,
    string SourceEmployeeId,
    string TargetEmployeeId,
    IReadOnlyList<MemoryNamespace> SourceNamespaces,
    MemoryNamespace TargetNamespace,
    string Debrief,
    IReadOnlyList<KnowledgeTransferItem> Items,
    MemorySensitivity DebriefSensitivity,
    KnowledgeTransferStatus Status,
    DateTimeOffset CreatedAt,
    string CreatedByEmployeeId,
    string? ApprovedByEmployeeId = null,
    DateTimeOffset? ApprovedAt = null,
    DateTimeOffset? AppliedAt = null,
    Guid? AppliedEpisodeId = null,
    string? ApprovalNotes = null)
{
    public MemoryTransferEvidence? ApprovedEvidence { get; init; }
}
