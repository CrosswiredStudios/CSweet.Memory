using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public static class Mem0AgentMemoryBuilderExtensions
{
    public static AgentMemoryBuilder UseMem0(this AgentMemoryBuilder builder, Uri endpoint, string? apiKey = null)
    {
        builder.Services.AddHttpClient<Mem0MemoryStore>();
        builder.Services.AddSingleton<IMemoryStore>(provider => new Mem0MemoryStore(provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Mem0MemoryStore)), endpoint, apiKey));
        return builder;
    }
}
