namespace CSweet.Memory;

public sealed class AgentMemoryOptions
{
    public MemoryScope DefaultScope { get; set; } = MemoryScope.User;
    public int ContextTokenBudget { get; set; } = 2_000;
    public int MaximumEpisodeCharacters { get; set; } = 100_000;
    public int RetrievalLimit { get; set; } = 30;
    public bool IncludePendingClaims { get; set; }
    public bool StoreAssistantMessages { get; set; } = true;
    public bool FailOpen { get; set; } = true;
    public ISet<string> ProtectedEntityTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Business", "Person", "Goal", "Role", "Task", "Resource"
    };
    public ISet<string> ProtectedRelationships { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "REQUIRES", "REPORTS_TO", "OWNS", "ASSIGNED_TO", "DEPENDS_ON"
    };
}

internal sealed class AllowAllMemoryScopeAuthorizer : IMemoryScopeAuthorizer
{
    public ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}

internal sealed class PassthroughMemoryRedactor : IMemoryRedactor
{
    public ValueTask<string> RedactAsync(string content, MemorySensitivity sensitivity, CancellationToken cancellationToken = default) => ValueTask.FromResult(content);
}
