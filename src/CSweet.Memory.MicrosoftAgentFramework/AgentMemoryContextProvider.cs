using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.Memory;

public interface IAgentMemoryPartitionResolver
{
    ValueTask<(MemoryPartition Partition, MemoryScope Scope)> ResolveAsync(AgentSession session, CancellationToken cancellationToken = default);
}

public static class AgentMemorySessionKeys
{
    public const string TenantId = "CSweet.Memory.TenantId";
    public const string ApplicationId = "CSweet.Memory.ApplicationId";
    public const string AgentId = "CSweet.Memory.AgentId";
    public const string UserId = "CSweet.Memory.UserId";
    public const string ConversationId = "CSweet.Memory.ConversationId";
    public const string CustomNamespace = "CSweet.Memory.CustomNamespace";
    public const string Scope = "CSweet.Memory.Scope";
    public const string LastInvocationId = "CSweet.Memory.LastInvocationId";

    public static AgentSession ConfigureMemory(
        this AgentSession session,
        MemoryPartition partition,
        MemoryScope scope = MemoryScope.User)
    {
        session.StateBag.SetValue(TenantId, partition.TenantId);
        SetOptional(session, ApplicationId, partition.ApplicationId);
        SetOptional(session, AgentId, partition.AgentId);
        SetOptional(session, UserId, partition.UserId);
        SetOptional(session, ConversationId, partition.ConversationId);
        SetOptional(session, CustomNamespace, partition.CustomNamespace);
        session.StateBag.SetValue(Scope, scope.ToString());
        return session;
    }

    private static void SetOptional(AgentSession session, string key, string? value)
    {
        if (value is not null) session.StateBag.SetValue(key, value);
    }
}

public sealed class SessionStateMemoryPartitionResolver : IAgentMemoryPartitionResolver
{
    private readonly AgentMemoryOptions _options;
    public SessionStateMemoryPartitionResolver(IOptions<AgentMemoryOptions> options) => _options = options.Value;

    public ValueTask<(MemoryPartition Partition, MemoryScope Scope)> ResolveAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        var tenantId = session.StateBag.GetValue<string>(AgentMemorySessionKeys.TenantId);
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException($"Agent session memory requires '{AgentMemorySessionKeys.TenantId}'.");
        var partition = new MemoryPartition(
            tenantId,
            GetOptional(session, AgentMemorySessionKeys.ApplicationId),
            GetOptional(session, AgentMemorySessionKeys.AgentId),
            GetOptional(session, AgentMemorySessionKeys.UserId),
            GetOptional(session, AgentMemorySessionKeys.ConversationId),
            GetOptional(session, AgentMemorySessionKeys.CustomNamespace));
        var scope = session.StateBag.TryGetValue<string>(AgentMemorySessionKeys.Scope, out var configured) &&
            Enum.TryParse<MemoryScope>(configured, ignoreCase: true, out var parsed)
                ? parsed : _options.DefaultScope;
        return ValueTask.FromResult((partition, scope));
    }

    private static string? GetOptional(AgentSession session, string key) =>
        session.StateBag.TryGetValue<string>(key, out var value) ? value : null;
}

public sealed class AgentMemoryContextProvider : AIContextProvider
{
    private readonly IMemoryEngine _memory;
    private readonly IAgentMemoryPartitionResolver _partitionResolver;
    private readonly AgentMemoryOptions _options;

    public AgentMemoryContextProvider(
        IMemoryEngine memory,
        IAgentMemoryPartitionResolver partitionResolver,
        IOptions<AgentMemoryOptions> options)
    {
        _memory = memory;
        _partitionResolver = partitionResolver;
        _options = options.Value;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = context.Session ?? throw new InvalidOperationException("Durable memory requires an AgentSession.");
            var (partition, scope) = await _partitionResolver.ResolveAsync(session, cancellationToken);
            var query = string.Join('\n', context.AIContext.Messages?.Select(message => message.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
            if (string.IsNullOrWhiteSpace(query)) return new AIContext();
            var packet = await _memory.RecallAsync(new MemoryRecallRequest(partition, scope, query, TokenBudget: _options.ContextTokenBudget), cancellationToken);
            session.StateBag.SetValue(AgentMemorySessionKeys.LastInvocationId, packet.InvocationId);
            return packet.Items.Count == 0
                ? new AIContext()
                : new AIContext { Messages = [new ChatMessage(ChatRole.User, packet.RenderedContext)] };
        }
        catch when (_options.FailOpen)
        {
            return new AIContext();
        }
    }

    protected override async ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var session = context.Session ?? throw new InvalidOperationException("Durable memory requires an AgentSession.");
            var (partition, scope) = await _partitionResolver.ResolveAsync(session, cancellationToken);
            foreach (var message in context.RequestMessages.Where(message => !string.IsNullOrWhiteSpace(message.Text)))
            {
                await _memory.IngestAsync(new MemoryIngestRequest(
                    partition, scope, message.Text!, new MemorySource("user", Guid.NewGuid().ToString("N"), message.Role.ToString()),
                    Metadata: new Dictionary<string, string> { ["role"] = message.Role.ToString() }), cancellationToken);
            }
            if (_options.StoreAssistantMessages && context.ResponseMessages is not null)
            {
                foreach (var message in context.ResponseMessages.Where(message => !string.IsNullOrWhiteSpace(message.Text)))
                {
                    await _memory.IngestAsync(new MemoryIngestRequest(
                        partition, scope, message.Text!, new MemorySource("assistant", Guid.NewGuid().ToString("N"), message.Role.ToString()),
                        Metadata: new Dictionary<string, string> { ["role"] = message.Role.ToString() }), cancellationToken);
                }
            }
        }
        catch when (_options.FailOpen)
        {
        }
    }
}

public static class AgentMemoryContextProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAgentMemoryContextProvider(this IServiceCollection services)
    {
        services.AddSingleton<IAgentMemoryPartitionResolver, SessionStateMemoryPartitionResolver>();
        services.AddSingleton<AgentMemoryContextProvider>();
        services.AddSingleton<AIContextProvider>(provider => provider.GetRequiredService<AgentMemoryContextProvider>());
        return services;
    }
}
