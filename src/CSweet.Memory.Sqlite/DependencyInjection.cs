using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public static class SqliteAgentMemoryBuilderExtensions
{
    public static AgentMemoryBuilder UseIntegratedSqlite(this AgentMemoryBuilder builder, string databasePath)
    {
        builder.Services.AddSingleton<IMemoryStore>(_ => new SqliteMemoryStore(databasePath));
        return builder;
    }
}
