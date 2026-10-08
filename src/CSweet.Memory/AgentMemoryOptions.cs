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

public sealed class DenyAllMemoryScopeAuthorizer : IMemoryScopeAuthorizer
{
    public ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}

/// <summary>Explicitly delegates authorization to a trusted downstream store, such as the C-Sweet broker.</summary>
public sealed class DelegatedMemoryScopeAuthorizer : IMemoryScopeAuthorizer
{
    public ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(access is not null && access.Principal.TenantId == partition.TenantId);
    public ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(access is not null && access.Principal.TenantId == partition.TenantId);
}

/// <summary>Intended only for isolated development and tests.</summary>
public sealed class AllowAllMemoryScopeAuthorizer : IMemoryScopeAuthorizer
{
    public ValueTask<bool> CanReadAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask<bool> CanWriteAsync(MemoryPartition partition, MemoryScope scope, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}

public sealed class SafeMemoryRedactor : IMemoryRedactor
{
    public ValueTask<string> RedactAsync(string content, MemorySensitivity sensitivity, MemoryAccessContext? access, CancellationToken cancellationToken = default)
    {
        var maximum = MemorySensitivity.Internal;
        if (access?.Principal.Attributes?.TryGetValue("memory.maxSensitivity", out var configured) == true &&
            Enum.TryParse<MemorySensitivity>(configured, ignoreCase: true, out var parsed)) maximum = parsed;
        else if (access?.Principal.Attributes?.TryGetValue("memory.sensitive.read", out var legacy) == true &&
            bool.TryParse(legacy, out var allowed) && allowed) maximum = MemorySensitivity.Restricted;
        return ValueTask.FromResult(sensitivity <= maximum
            ? content
            : $"[REDACTED {sensitivity} MEMORY]");
    }
}

public sealed class PassthroughMemoryRedactor : IMemoryRedactor
{
    public ValueTask<string> RedactAsync(string content, MemorySensitivity sensitivity, MemoryAccessContext? access, CancellationToken cancellationToken = default) => ValueTask.FromResult(content);
}

public sealed class PrimaryMemoryNamespaceResolver : IMemoryNamespaceResolver
{
    public ValueTask<IReadOnlyList<MemoryNamespace>> ResolveReadableNamespacesAsync(MemoryPartition primaryPartition, MemoryScope primaryScope, MemoryAccessContext? access, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryNamespace>>([
            new(primaryPartition, primaryScope, ToAudience(primaryScope), AudienceId(primaryPartition, primaryScope))
        ]);

    private static MemoryAudienceType ToAudience(MemoryScope scope) => scope switch
    {
        MemoryScope.Tenant => MemoryAudienceType.Organization,
        MemoryScope.Agent => MemoryAudienceType.Employee,
        MemoryScope.User => MemoryAudienceType.UserRelationship,
        MemoryScope.Conversation => MemoryAudienceType.Conversation,
        _ => MemoryAudienceType.Custom
    };

    private static string AudienceId(MemoryPartition partition, MemoryScope scope) => scope switch
    {
        MemoryScope.Tenant => partition.TenantId,
        MemoryScope.Agent => partition.AgentId ?? partition.TenantId,
        MemoryScope.User => partition.UserId ?? partition.TenantId,
        MemoryScope.Conversation => partition.ConversationId ?? partition.TenantId,
        _ => partition.CustomNamespace ?? partition.Key
    };
}

public static class EmployeeMemoryNamespaces
{
    public static MemoryNamespace Organization(string tenantId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, CustomNamespace: "organization"), MemoryScope.Tenant, MemoryAudienceType.Organization, tenantId);

    public static MemoryNamespace Team(string tenantId, string teamId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, CustomNamespace: $"team:{teamId}"), MemoryScope.Custom, MemoryAudienceType.Team, teamId);

    public static MemoryNamespace Role(string tenantId, string roleId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, CustomNamespace: $"role:{roleId}"), MemoryScope.Custom, MemoryAudienceType.Role, roleId);

    public static MemoryNamespace Employee(string tenantId, string employeeId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, employeeId, CustomNamespace: $"employee:{employeeId}"), MemoryScope.Agent, MemoryAudienceType.Employee, employeeId);

    public static MemoryNamespace UserRelationship(string tenantId, string employeeId, string userId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, employeeId, userId, CustomNamespace: $"relationship:{employeeId}:{userId}"), MemoryScope.User, MemoryAudienceType.UserRelationship, userId);

    public static MemoryNamespace Case(string tenantId, string caseId, string? applicationId = null) =>
        new(new MemoryPartition(tenantId, applicationId, CustomNamespace: $"case:{caseId}"), MemoryScope.Custom, MemoryAudienceType.Case, caseId);
}

public sealed class WorkContextMemoryNamespaceResolver : IMemoryNamespaceResolver
{
    public ValueTask<IReadOnlyList<MemoryNamespace>> ResolveReadableNamespacesAsync(MemoryPartition primaryPartition, MemoryScope primaryScope, MemoryAccessContext? access, CancellationToken cancellationToken = default)
    {
        if (access is null) return new PrimaryMemoryNamespaceResolver().ResolveReadableNamespacesAsync(primaryPartition, primaryScope, null, cancellationToken);
        var principal = access.Principal;
        var namespaces = new List<MemoryNamespace>
        {
            new(primaryPartition, primaryScope, PrimaryAudience(primaryScope), primaryPartition.Key),
            EmployeeMemoryNamespaces.Organization(principal.TenantId, primaryPartition.ApplicationId),
            EmployeeMemoryNamespaces.Employee(principal.TenantId, principal.EmployeeId, primaryPartition.ApplicationId)
        };
        foreach (var roleId in (principal.RoleIds ?? new HashSet<string>()).Append(access.WorkContext?.RoleId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            namespaces.Add(EmployeeMemoryNamespaces.Role(principal.TenantId, roleId!, primaryPartition.ApplicationId));
        foreach (var teamId in (principal.TeamIds ?? new HashSet<string>()).Append(access.WorkContext?.TeamId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            namespaces.Add(EmployeeMemoryNamespaces.Team(principal.TenantId, teamId!, primaryPartition.ApplicationId));
        if (!string.IsNullOrWhiteSpace(access.WorkContext?.CaseId))
            namespaces.Add(EmployeeMemoryNamespaces.Case(principal.TenantId, access.WorkContext.CaseId, primaryPartition.ApplicationId));
        return ValueTask.FromResult<IReadOnlyList<MemoryNamespace>>(namespaces.DistinctBy(item => item.Partition.StorageKey).ToList());
    }

    private static MemoryAudienceType PrimaryAudience(MemoryScope scope) => scope switch
    {
        MemoryScope.Tenant => MemoryAudienceType.Organization,
        MemoryScope.Agent => MemoryAudienceType.Employee,
        MemoryScope.User => MemoryAudienceType.UserRelationship,
        MemoryScope.Conversation => MemoryAudienceType.Conversation,
        _ => MemoryAudienceType.Custom
    };
}
