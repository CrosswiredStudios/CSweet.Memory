# CSweet.Memory

`CSweet.Memory` is a first-party temporal memory framework for .NET agents. It stores immutable source episodes, derives provenance-bearing claims and relationships, retrieves context with hybrid rank fusion, and integrates with Microsoft Agent Framework through `AIContextProvider`.

The framework owns its complete memory pipeline and does not wrap or depend on another agent-memory product. SQLite provides embedded local storage and PostgreSQL provides production storage. Both implement the same temporal property-graph model without requiring a graph database.

## Employee memory model

C-Sweet operational records remain authoritative for employees, roles, teams, assignments, objectives, tasks, cases, and approvals. Memory stores evidence-backed observations and experience about those records through stable `MemoryOperationalReference` and entity application keys; it is not a competing HR or workflow database.

Employee memory distinguishes facts, observations, decisions, commitments, outcomes, handoffs, feedback, failures, demonstrated skills, and open questions. Retrieval can combine authorized organization, team, role, employee, user-relationship, case, and conversation namespaces. Authorization is evaluated before each namespace is searched.

The engine denies reads and writes by default and redacts content above the caller's explicit `memory.maxSensitivity` attribute. Applications must register an `IMemoryScopeAuthorizer`. `DelegatedMemoryScopeAuthorizer` is intended only when a trusted downstream broker performs the definitive policy check; `AllowAllMemoryScopeAuthorizer` is for isolated tests and local development.

```csharp
services.AddAgentMemory(options =>
    {
        options.DefaultScope = MemoryScope.User;
        options.ContextTokenBudget = 2_000;
    })
    .UseIntegratedSqlite("memory.db")
    .UseOptionalEnrichment();

services.AddAgentMemoryContextProvider();
```

## Knowledge transfer

Replacing an employee is an approval-gated workflow rather than a bulk copy of private history:

1. `PrepareKnowledgeTransferAsync` builds an inspectable debrief package from authorized source namespaces.
2. Sensitivity, layer, selected-memory, and token-budget filters are applied before the package is persisted.
3. `ApproveKnowledgeTransferAsync` records an approval or rejection and its reviewer.
4. `ApplyKnowledgeTransferAsync` creates one provenance-bearing episode in the replacement employee's namespace and queues normal enrichment.

Raw episodic history is excluded by default. A transfer normally contains active semantic knowledge, curated core memory, and confirmed procedures. Episodic history must be selected explicitly. Restricted content cannot be included when the transfer's maximum sensitivity is lower, and the target must be an employee namespace matching the replacement employee.

```csharp
var package = await memory.PrepareKnowledgeTransferAsync(new(
    SourceEmployeeId: "employee-old",
    TargetEmployeeId: "employee-new",
    SourceNamespaces: [EmployeeMemoryNamespaces.Employee(tenantId, "employee-old")],
    TargetNamespace: EmployeeMemoryNamespaces.Employee(tenantId, "employee-new"),
    Access: managerAccess,
    Debrief: "Open work, key decisions, recurring risks, and important relationships."));

package = await memory.ApproveKnowledgeTransferAsync(
    new(package.Id, managerAccess, Approved: true));
package = await memory.ApplyKnowledgeTransferAsync(
    new(package.Id, managerAccess));
```

The vendor-neutral packages target .NET 8 or later. `CSweet.Memory.Broker` currently targets .NET 10 because the C-Sweet Agent SDK targets .NET 10. All packages are licensed under Apache-2.0.

## Creating NuGet packages

Run the batch file from the repository root to restore published dependencies, run the test suite, and create all six packages in a versioned directory such as `artifacts\packages\0.1.1`:

```bat
Create-NuGetPackages.bat
```

Pass a version and optional output root to override the repository defaults. The version directory is appended automatically, so this example writes to `C:\packages\csweet-memory\0.1.2`:

```bat
Create-NuGetPackages.bat 0.1.2 C:\packages\csweet-memory
```
