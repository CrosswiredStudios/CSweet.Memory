# CSweet.Memory

`CSweet.Memory` is a vendor-neutral temporal memory framework for .NET agents. It stores immutable source episodes, derives provenance-bearing claims and relationships, retrieves context with hybrid rank fusion, and integrates with Microsoft Agent Framework through `AIContextProvider`.

The first-party Integrated stores use SQLite for local development and PostgreSQL for production. Mem0 and Neo4j are optional connectors; neither is required.

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

The vendor-neutral packages target .NET 8 or later. `CSweet.Memory.CSweet` currently targets .NET 10 because the C-Sweet Agent SDK targets .NET 10. All packages are licensed under Apache-2.0.
