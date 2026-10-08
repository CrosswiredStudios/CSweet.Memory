using System.Text.Json;

namespace CSweet.Memory.Tests;

public sealed class MemoryClassificationTests
{
    [Fact]
    public void LegacyPayloadsWithoutSensitivityDefaultToRestricted()
    {
        const string entityJson = """
            {"id":"00000000-0000-0000-0000-000000000001","partition":{"tenantId":"tenant"},
             "type":"topic","canonicalName":"private name","aliases":[],"isProtectedType":false,
             "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}
            """;
        const string blockJson = """
            {"id":"00000000-0000-0000-0000-000000000002","partition":{"tenantId":"tenant"},
             "name":"private","content":"private block","revision":1,"maximumTokens":100,"isPinned":true,
             "trust":1,"updatedAt":"2026-01-01T00:00:00Z"}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var entity = JsonSerializer.Deserialize<MemoryEntity>(entityJson, options)!;
        var block = JsonSerializer.Deserialize<MemoryBlock>(blockJson, options)!;
        Assert.Equal(MemorySensitivity.Restricted, entity.Sensitivity);
        Assert.Equal(MemorySensitivity.Restricted, block.Sensitivity);
        var projection = MemoryReadProjection.Create(new("1.0", [], [entity], [], [], [block], []),
            entity.Partition, MemorySensitivity.Personal, DateTimeOffset.UtcNow);
        Assert.Empty(projection.Entities);
        Assert.Empty(projection.Blocks);
    }
}
