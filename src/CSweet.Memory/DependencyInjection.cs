using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public sealed class AgentMemoryBuilder
{
    public AgentMemoryBuilder(IServiceCollection services) => Services = services;
    public IServiceCollection Services { get; }
}

public static class AgentMemoryServiceCollectionExtensions
{
    public static AgentMemoryBuilder AddAgentMemory(this IServiceCollection services, Action<AgentMemoryOptions>? configure = null)
    {
        if (configure is null) services.AddOptions<AgentMemoryOptions>();
        else services.Configure(configure);
        services.AddSingleton<IMemoryScopeAuthorizer, AllowAllMemoryScopeAuthorizer>();
        services.AddSingleton<IMemoryRedactor, PassthroughMemoryRedactor>();
        services.AddSingleton<MemoryEnrichmentWorker>();
        services.AddSingleton<IMemoryEnrichmentQueue>(provider => provider.GetRequiredService<MemoryEnrichmentWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<MemoryEnrichmentWorker>());
        services.AddSingleton<IMemoryEngine, MemoryEngine>();
        return new AgentMemoryBuilder(services);
    }

    public static AgentMemoryBuilder UseOptionalEnrichment<TEnricher>(this AgentMemoryBuilder builder)
        where TEnricher : class, IMemoryEnricher
    {
        builder.Services.AddSingleton<IMemoryEnricher, TEnricher>();
        return builder;
    }

    public static AgentMemoryBuilder UseOptionalEnrichment(this AgentMemoryBuilder builder) => builder;
}
