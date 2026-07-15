using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed class EmployeeMemoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"csweet-employee-memory-{Guid.NewGuid():N}.db");
    private SqliteMemoryStore _store = null!;
    private static readonly MemoryAccessContext ManagerAccess = new(
        new MemoryPrincipal("tenant-a", "manager-1", RoleIds: new HashSet<string> { "manager" }),
        "employee-transition",
        "manage-memory");

    public async Task InitializeAsync()
    {
        _store = new SqliteMemoryStore(_path);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }

    [Fact]
    public async Task Engine_DeniesAccessByDefault()
    {
        var engine = new MemoryEngine(_store, Options.Create(new AgentMemoryOptions()));
        var partition = EmployeePartition("employee-1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.IngestAsync(new MemoryIngestRequest(
            partition, MemoryScope.Agent, "private observation", new MemorySource("user", "1"), Access: ManagerAccess)));
    }

    [Fact]
    public async Task Recall_FusesAuthorizedOrganizationAndEmployeeNamespaces()
    {
        var organization = new MemoryNamespace(new MemoryPartition("tenant-a", CustomNamespace: "organization"), MemoryScope.Tenant, MemoryAudienceType.Organization, "tenant-a");
        var employee = new MemoryNamespace(EmployeePartition("employee-1"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-1");
        var engine = CreateEngine(new FixedNamespaceResolver([organization, employee]));
        await engine.IngestAsync(new MemoryIngestRequest(organization.Partition, organization.Scope, "North-star revenue objective", new MemorySource("application", "objective-1"), Access: ManagerAccess));
        await engine.IngestAsync(new MemoryIngestRequest(employee.Partition, employee.Scope, "Revenue reporting closes Friday", new MemorySource("employee", "journal-1"), Access: ManagerAccess));

        var packet = await engine.RecallAsync(new MemoryRecallRequest(employee.Partition, employee.Scope, "revenue", Access: ManagerAccess));

        Assert.Equal(2, packet.Items.Count);
        Assert.Contains(packet.Items, item => item.Content.Contains("North-star", StringComparison.Ordinal));
        Assert.Contains(packet.Items, item => item.Content.Contains("Friday", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SafeRedactor_HidesSensitiveEmployeeMemoryWithoutGrant()
    {
        var partition = EmployeePartition("employee-1");
        var engine = new MemoryEngine(_store, Options.Create(new AgentMemoryOptions()), authorizer: new AllowAllMemoryScopeAuthorizer());
        await engine.IngestAsync(new MemoryIngestRequest(
            partition, MemoryScope.Agent, "Medical accommodation details", new MemorySource("employee", "private-1"),
            Access: ManagerAccess, Sensitivity: MemorySensitivity.Restricted));

        var packet = await engine.RecallAsync(new MemoryRecallRequest(partition, MemoryScope.Agent, "medical accommodation", Access: ManagerAccess));

        Assert.Single(packet.Items);
        Assert.Equal("[REDACTED Restricted MEMORY]", packet.Items[0].Content);
        Assert.DoesNotContain("Medical accommodation details", packet.RenderedContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeTransfer_IsFilteredApprovedAuditedAndApplied()
    {
        var source = new MemoryNamespace(EmployeePartition("employee-old"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-old");
        var target = new MemoryNamespace(EmployeePartition("employee-new"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-new");
        var engine = CreateEngine();
        var transferable = await engine.IngestAsync(new MemoryIngestRequest(
            source.Partition, source.Scope, "Escalation checklist: contact the account owner before finance.",
            new MemorySource("employee", "debrief-1"), Access: ManagerAccess));
        await engine.IngestAsync(new MemoryIngestRequest(
            source.Partition, source.Scope, "Restricted executive credential", new MemorySource("employee", "secret-1"),
            Access: ManagerAccess, Sensitivity: MemorySensitivity.Restricted));

        var package = await engine.PrepareKnowledgeTransferAsync(new PrepareKnowledgeTransferRequest(
            "employee-old", "employee-new", [source], target, ManagerAccess,
            "Outgoing employee completed a structured operational debrief.", MaximumSensitivity: MemorySensitivity.Confidential,
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));

        Assert.Equal(KnowledgeTransferStatus.PendingApproval, package.Status);
        Assert.Contains(package.Items, item => item.MemoryId == transferable.Id);
        Assert.DoesNotContain(package.Items, item => item.Content.Contains("credential", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ApplyKnowledgeTransferAsync(new(package.Id, ManagerAccess)));

        package = await engine.ApproveKnowledgeTransferAsync(new(package.Id, ManagerAccess, Approved: true, "Reviewed by operations."));
        package = await engine.ApplyKnowledgeTransferAsync(new(package.Id, ManagerAccess));

        Assert.Equal(KnowledgeTransferStatus.Applied, package.Status);
        Assert.NotNull(package.AppliedEpisodeId);
        var targetRecall = await engine.RecallAsync(new MemoryRecallRequest(target.Partition, target.Scope, "escalation checklist", Access: ManagerAccess));
        Assert.Single(targetRecall.Items);
        Assert.Contains($"memory:{transferable.Id:N}", targetRecall.Items[0].Content, StringComparison.Ordinal);
        var persisted = await _store.GetKnowledgeTransferAsync(package.Id);
        Assert.Equal(KnowledgeTransferStatus.Applied, persisted?.Status);
    }

    [Fact]
    public async Task KnowledgeTransfer_ExcludesRawEpisodesByDefault()
    {
        var source = new MemoryNamespace(EmployeePartition("employee-old"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-old");
        var target = new MemoryNamespace(EmployeePartition("employee-new"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-new");
        var engine = CreateEngine();
        await engine.IngestAsync(new MemoryIngestRequest(
            source.Partition, source.Scope, "Uncurated conversation transcript", new MemorySource("employee", "raw-1"), Access: ManagerAccess));

        var package = await engine.PrepareKnowledgeTransferAsync(new PrepareKnowledgeTransferRequest(
            "employee-old", "employee-new", [source], target, ManagerAccess, "Curated debrief only."));

        Assert.Empty(package.Items);
    }

    [Fact]
    public async Task KnowledgeTransfer_RejectsCrossTenantTarget()
    {
        var source = new MemoryNamespace(EmployeePartition("employee-old"), MemoryScope.Agent, MemoryAudienceType.Employee, "employee-old");
        var target = EmployeeMemoryNamespaces.Employee("tenant-b", "employee-new", "csweet");
        var engine = CreateEngine();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.PrepareKnowledgeTransferAsync(new PrepareKnowledgeTransferRequest(
            "employee-old", "employee-new", [source], target, ManagerAccess, "Attempted cross-tenant transfer.")));
    }

    [Fact]
    public async Task ApplicationKey_PreservesEmployeeIdentityAcrossRename()
    {
        var partition = EmployeePartition("employee-1");
        var first = new MemoryEntity(Guid.NewGuid(), partition, "Person", "Alex Smith", [], "employee:42", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await _store.UpsertEntityAsync(first);
        var renamed = first with { Id = Guid.NewGuid(), CanonicalName = "Alex Morgan", UpdatedAt = DateTimeOffset.UtcNow };

        var result = await _store.UpsertEntityAsync(renamed);
        var resolved = await _store.FindEntityByApplicationKeyAsync(partition, "employee:42");

        Assert.False(result.Created);
        Assert.Equal(first.Id, resolved?.Id);
        Assert.Equal("Alex Morgan", resolved?.CanonicalName);
    }

    private MemoryEngine CreateEngine(IMemoryNamespaceResolver? resolver = null) => new(
        _store,
        Options.Create(new AgentMemoryOptions { ContextTokenBudget = 2_000 }),
        authorizer: new AllowAllMemoryScopeAuthorizer(),
        redactor: new PassthroughMemoryRedactor(),
        namespaceResolver: resolver);

    private static MemoryPartition EmployeePartition(string employeeId) =>
        new("tenant-a", "csweet", employeeId, CustomNamespace: $"employee:{employeeId}");

    private sealed class FixedNamespaceResolver(IReadOnlyList<MemoryNamespace> namespaces) : IMemoryNamespaceResolver
    {
        public ValueTask<IReadOnlyList<MemoryNamespace>> ResolveReadableNamespacesAsync(MemoryPartition primaryPartition, MemoryScope primaryScope, MemoryAccessContext? access, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(namespaces);
    }
}
