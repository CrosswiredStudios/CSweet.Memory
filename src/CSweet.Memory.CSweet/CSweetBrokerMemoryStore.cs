using System.Text.Json;
using CSweet.Agent.Contracts.Grpc;
using CSweet.Agent.SDK;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public static class CSweetMemoryCapabilities
{
    public const string Query = "platform.memory.query.v1";
    public const string Write = "platform.memory.write.v1";
    public const string Manage = "platform.memory.manage.v1";
    public const string Export = "platform.memory.export.v1";
}

public sealed record CSweetMemoryCommand(string Operation, JsonElement Payload);

public sealed class CSweetBrokerMemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAgentBrokerClient _broker;

    public CSweetBrokerMemoryStore(IAgentBrokerClient broker) => _broker = broker;

    public MemoryStoreCapabilities Capabilities => MemoryStoreCapabilities.Transactions |
        MemoryStoreCapabilities.FullText | MemoryStoreCapabilities.NativeVectors |
        MemoryStoreCapabilities.RecursiveTraversal | MemoryStoreCapabilities.TemporalQueries |
        MemoryStoreCapabilities.BulkOperations | MemoryStoreCapabilities.ChangeHistory;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("append-episode", episode, cancellationToken);
    public Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("upsert-entity", entity, cancellationToken);
    public Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default) => QueryAsync<MemoryEntity?>("find-entity", new { partition, canonicalName }, cancellationToken);
    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("write-claim", claim, cancellationToken);
    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("write-edge", edge, cancellationToken);
    public Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("write-block", block, cancellationToken);
    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("write-procedure", procedure, cancellationToken);
    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) => WriteAsync<MemoryWriteResult>("write-embedding", embedding, cancellationToken);
    public async Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default) => await WriteAsync<MemoryWriteResult>("record-use", use, cancellationToken);
    public Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default) => QueryAsync<IReadOnlyList<MemoryCandidate>>("search", request, cancellationToken);
    public async Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default) => await ManageAsync<MemoryWriteResult>("supersede-claim", new { claimId, supersededByClaimId, validTo }, cancellationToken);
    public Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default) => QueryAsync<MemoryClaim?>("get-claim", new { claimId }, cancellationToken);
    public async Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default) => await ManageAsync<MemoryWriteResult>("set-confirmation", new { claimId, confirmation }, cancellationToken);
    public Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => QueryAsync<IReadOnlyList<MemoryClaim>>("list-claims", partition, cancellationToken);
    public Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => InvokeAsync<MemoryExport>(CSweetMemoryCapabilities.Export, "export", partition, cancellationToken);
    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => await ManageAsync<MemoryWriteResult>("delete-scope", partition, cancellationToken);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<T> WriteAsync<T>(string operation, object payload, CancellationToken cancellationToken) => InvokeAsync<T>(CSweetMemoryCapabilities.Write, operation, payload, cancellationToken);
    private Task<T> QueryAsync<T>(string operation, object payload, CancellationToken cancellationToken) => InvokeAsync<T>(CSweetMemoryCapabilities.Query, operation, payload, cancellationToken);
    private Task<T> ManageAsync<T>(string operation, object payload, CancellationToken cancellationToken) => InvokeAsync<T>(CSweetMemoryCapabilities.Manage, operation, payload, cancellationToken);

    private async Task<T> InvokeAsync<T>(string capability, string operation, object payload, CancellationToken cancellationToken)
    {
        var command = new CSweetMemoryCommand(operation, JsonSerializer.SerializeToElement(payload, JsonOptions));
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = capability,
            ContentType = "application/json",
            Payload = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions))
        };
        var result = await _broker.InvokeCapabilityAsync(request, request.RequestId, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? $"C-Sweet memory capability '{capability}' failed." : result.Error);
        return JsonSerializer.Deserialize<T>(result.Payload.Span, JsonOptions)
            ?? throw new InvalidOperationException($"C-Sweet memory capability '{capability}' returned an empty payload.");
    }
}

public static class CSweetAgentMemoryBuilderExtensions
{
    public static AgentMemoryBuilder UseCSweetBroker(this AgentMemoryBuilder builder)
    {
        builder.Services.AddSingleton<IMemoryStore, CSweetBrokerMemoryStore>();
        return builder;
    }
}
