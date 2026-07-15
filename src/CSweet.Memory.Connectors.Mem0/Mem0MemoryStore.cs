using System.Net.Http.Json;
using System.Text.Json;

namespace CSweet.Memory;

public sealed class Mem0MemoryStore : IMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public Mem0MemoryStore(HttpClient httpClient, Uri? endpoint = null, string? apiKey = null)
    {
        _httpClient = httpClient;
        if (endpoint is not null) _httpClient.BaseAddress = endpoint;
        if (!string.IsNullOrWhiteSpace(apiKey)) _httpClient.DefaultRequestHeaders.Authorization = new("Token", apiKey);
    }

    public MemoryStoreCapabilities Capabilities => MemoryStoreCapabilities.FullText |
        MemoryStoreCapabilities.NativeVectors | MemoryStoreCapabilities.ChangeHistory |
        MemoryStoreCapabilities.BulkOperations;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<MemoryWriteResult> AppendEpisodeAsync(MemoryEpisode episode, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            messages = new[] { new { role = episode.Source.Author ?? "user", content = episode.Content } },
            user_id = episode.Partition.UserId,
            agent_id = episode.Partition.AgentId,
            app_id = episode.Partition.ApplicationId,
            run_id = episode.Partition.ConversationId,
            metadata = new
            {
                csweet_memory_id = episode.Id,
                tenant_id = episode.Partition.TenantId,
                scope = episode.Scope.ToString(),
                source_type = episode.Source.Type,
                source_id = episode.Source.Id,
                occurred_at = episode.OccurredAt
            }
        };
        using var response = await _httpClient.PostAsJsonAsync("v1/memories/", body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new MemoryWriteResult(episode.Id, true);
    }

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            query = request.Query,
            filters = new
            {
                user_id = request.Partition.UserId,
                agent_id = request.Partition.AgentId,
                app_id = request.Partition.ApplicationId,
                run_id = request.Partition.ConversationId
            },
            top_k = request.Limit
        };
        using var response = await _httpClient.PostAsJsonAsync("v2/memories/search/", body, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("results", out var results) ? results : default;
        if (array.ValueKind != JsonValueKind.Array) return [];
        var candidates = new List<MemoryCandidate>();
        foreach (var item in array.EnumerateArray())
        {
            var text = item.TryGetProperty("memory", out var memory) ? memory.GetString() : item.TryGetProperty("text", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var idText = item.TryGetProperty("id", out var id) ? id.GetString() : null;
            var memoryId = Guid.TryParse(idText, out var parsed) ? parsed : StableGuid(idText ?? text);
            var score = item.TryGetProperty("score", out var scoreElement) && scoreElement.TryGetDouble(out var parsedScore) ? parsedScore : 1d;
            candidates.Add(new MemoryCandidate(memoryId, MemoryLayer.Semantic, text, score,
                MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
                null, null, [], "mem0"));
        }
        return candidates;
    }

    public Task<MemoryWriteResult> UpsertEntityAsync(MemoryEntity entity, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task<MemoryEntity?> FindEntityAsync(MemoryPartition partition, string canonicalName, CancellationToken cancellationToken = default) => Unsupported<MemoryEntity?>();
    public Task<MemoryWriteResult> WriteClaimAsync(MemoryClaim claim, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task<MemoryWriteResult> WriteEdgeAsync(MemoryEdge edge, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task<MemoryWriteResult> WriteBlockAsync(MemoryBlock block, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task<MemoryWriteResult> WriteProcedureAsync(ProceduralMemory procedure, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task<MemoryWriteResult> WriteEmbeddingAsync(MemoryEmbedding embedding, CancellationToken cancellationToken = default) => Unsupported<MemoryWriteResult>();
    public Task RecordUseAsync(MemoryUse use, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SupersedeClaimAsync(Guid claimId, Guid supersededByClaimId, DateTimeOffset validTo, CancellationToken cancellationToken = default) => Unsupported();
    public Task<MemoryClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken = default) => Unsupported<MemoryClaim?>();
    public Task SetClaimConfirmationAsync(Guid claimId, MemoryConfirmationState confirmation, CancellationToken cancellationToken = default) => Unsupported();
    public Task<IReadOnlyList<MemoryClaim>> ListClaimsAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryClaim>>([]);
    public Task<MemoryExport> ExportAsync(MemoryPartition partition, CancellationToken cancellationToken = default) => Unsupported<MemoryExport>();

    public async Task DeleteScopeAsync(MemoryPartition partition, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (partition.UserId is not null) query.Add($"user_id={Uri.EscapeDataString(partition.UserId)}");
        if (partition.AgentId is not null) query.Add($"agent_id={Uri.EscapeDataString(partition.AgentId)}");
        if (partition.ApplicationId is not null) query.Add($"app_id={Uri.EscapeDataString(partition.ApplicationId)}");
        using var response = await _httpClient.DeleteAsync("v1/memories/?" + string.Join('&', query), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Task Unsupported() => Task.FromException(new NotSupportedException("The Mem0 connector does not support this canonical operation."));
    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("The Mem0 connector does not support this canonical operation."));
    private static Guid StableGuid(string value)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
}
