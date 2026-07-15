using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace CSweet.Memory.Tests;

public sealed class AgentFrameworkIntegrationTests
{
    [Fact]
    public async Task SessionResolver_UsesExplicitPartitionAndScope()
    {
        var session = new TestAgentSession().ConfigureMemory(new MemoryPartition("tenant", "app", "agent", "user", "conversation"), MemoryScope.Conversation);
        var resolver = new SessionStateMemoryPartitionResolver(Options.Create(new AgentMemoryOptions()));

        var resolved = await resolver.ResolveAsync(session);

        Assert.Equal("tenant/app/agent/user/conversation", resolved.Partition.Key);
        Assert.Equal(MemoryScope.Conversation, resolved.Scope);
    }

    [Fact]
    public void Provider_IsAnAiContextProvider()
    {
        Assert.True(typeof(AIContextProvider).IsAssignableFrom(typeof(AgentMemoryContextProvider)));
    }

    private sealed class TestAgentSession : AgentSession;
}
