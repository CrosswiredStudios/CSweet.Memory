using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Memory;

public static class Neo4jAgentMemoryBuilderExtensions
{
    public static AgentMemoryBuilder UseNeo4j(this AgentMemoryBuilder builder, string uri, string user, string password, string database = "neo4j")
    {
        builder.Services.AddSingleton<IMemoryStore>(_ => new Neo4jMemoryStore(uri, user, password, database));
        return builder;
    }
}
