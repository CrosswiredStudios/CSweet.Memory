using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public static class PostgreSqlAgentMemoryBuilderExtensions
{
    public static AgentMemoryBuilder UseIntegratedPostgreSql(this AgentMemoryBuilder builder, string connectionString)
    {
        builder.Services.AddSingleton<IMemoryStore>(_ => new PostgreSqlMemoryStore(connectionString));
        return builder;
    }
}
