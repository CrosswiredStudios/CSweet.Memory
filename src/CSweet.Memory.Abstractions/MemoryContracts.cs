namespace CSweet.Memory;

[Flags]
public enum MemoryStoreCapabilities
{
    None = 0,
    Transactions = 1 << 0,
    FullText = 1 << 1,
    NativeVectors = 1 << 2,
    RecursiveTraversal = 1 << 3,
    TemporalQueries = 1 << 4,
    BulkOperations = 1 << 5,
    ChangeHistory = 1 << 6
}

public sealed record MemoryWriteResult(Guid Id, bool Created, string? Message = null);

public sealed record MemorySearchRequest(
    MemoryPartition Partition,
    MemoryScope Scope,
    string Query,
    int Limit = 20,
    DateTimeOffset? AsOf = null,
    IReadOnlySet<MemoryLayer>? Layers = null,
    IReadOnlyList<float>? Embedding = null,
    bool IncludePending = false,
    bool IncludeSuperseded = false);

public sealed record MemoryIngestRequest(
    MemoryPartition Partition,
    MemoryScope Scope,
    string Content,
    MemorySource Source,
    string ContentType = "text/plain",
    string? IdempotencyKey = null,
    DateTimeOffset? OccurredAt = null,
    DateTimeOffset? ExpiresAt = null,
    bool LegalHold = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryRecallRequest(
    MemoryPartition Partition,
    MemoryScope Scope,
    string Query,
    string? InvocationId = null,
    int? TokenBudget = null,
    DateTimeOffset? AsOf = null,
    IReadOnlySet<MemoryLayer>? Layers = null);

public sealed record MemoryExport(
    string SchemaVersion,
    IReadOnlyList<MemoryEpisode> Episodes,
    IReadOnlyList<MemoryEntity> Entities,
    IReadOnlyList<MemoryClaim> Claims,
    IReadOnlyList<MemoryEdge> Edges,
    IReadOnlyList<MemoryBlock> Blocks,
    IReadOnlyList<ProceduralMemory> Procedures,
    IReadOnlyList<MemoryEmbedding>? Embeddings = null);

public interface IMemoryStore : IAsyncDisposable
{
    MemoryStoreCapabilities Capabilities { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default);
    Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default);
    Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default);
    Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default);
    Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default);
    Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default);
    Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default);
    Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default);
}

public interface IMemoryEngine
{
    Task<MemoryEpisode> IngestAsync(MemoryIngestRequest request, CancellationToken cancellationToken = default);
    Task<MemoryContextPacket> RecallAsync(MemoryRecallRequest request, CancellationToken cancellationToken = default);
    Task<MemoryClaim> CorrectClaimAsync(Guid claimId, string replacementValue, MemorySource source, CancellationToken cancellationToken = default);
    Task ConfirmClaimAsync(Guid claimId, bool confirmed, CancellationToken cancellationToken = default);
    Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default);
    Task DeleteAsync(MemoryPartition partition, CancellationToken cancellationToken = default);
}

public interface IMemoryEnricher
{
    string Version { get; }
    Task<MemoryEnrichment> EnrichAsync(MemoryEpisode episode, CancellationToken cancellationToken = default);
}

public interface IMemoryEnrichmentQueue
{
    ValueTask EnqueueAsync(MemoryEpisode episode, CancellationToken cancellationToken = default);
}

public interface IMemoryQueryEmbedder
{
    string? Model { get; }
    ValueTask<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record ExtractedEntity(string Type, string Name, IReadOnlyList<string>? Aliases = null, string? ApplicationKey = null);
public sealed record ExtractedClaim(string SubjectName, string Predicate, string? ObjectName, string? Value, double Confidence, double Importance, MemorySensitivity Sensitivity);
public sealed record ExtractedEdge(string FromName, string Relationship, string ToName, double Confidence);
public sealed record ExtractedProcedure(string Name, string Procedure, string? Applicability = null);
public sealed record MemoryEnrichment(
    IReadOnlyList<ExtractedEntity> Entities,
    IReadOnlyList<ExtractedClaim> Claims,
    IReadOnlyList<ExtractedEdge> Edges,
    IReadOnlyList<ExtractedProcedure> Procedures,
    IReadOnlyList<float>? Embedding = null);

public interface IMemoryScopeAuthorizer
{
    ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, CancellationToken cancellationToken = default);
    ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, CancellationToken cancellationToken = default);
}

public interface IMemoryRedactor
{
    ValueTask<string> RedactAsync(string content, MemorySensitivity sensitivity, CancellationToken cancellationToken = default);
}

public interface IMemoryEncryptionProvider
{
    ValueTask<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default);
    ValueTask<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default);
}
