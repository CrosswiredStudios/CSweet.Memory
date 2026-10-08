using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class LexicalRetrievalTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;

    [Theory]
    [InlineData("", 0)]
    [InlineData("??? % _ !!!", 0)]
    [InlineData("what is the", 0)]
    [InlineData("CSM-42 src/Memory.cs release_v2", 3)]
    [InlineData("café 東京 alice", 3)]
    public void QuerySyntaxIsLiteralAndBounded(string query, int terms)
    {
        Assert.Equal(terms, MemoryLexicalQuery.Parse(query).Terms.Count);
        Assert.True(MemoryLexicalQuery.Parse(string.Join(' ', Enumerable.Range(0, 10000))).Terms.Count <= 12);
        Assert.DoesNotContain("*", MemoryLexicalQuery.Parse("alice* OR NOT (secret)").FullText);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NaturalLanguageGoldenCorpusAndTemporalBoundaries(string provider)
    {
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"memory-lexical-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(sqlitePath);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "lexical-eval-v1");
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var measurements = new List<object>();
        try
        {
            var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application,
                "Alice selected PostgreSQL for CSM-42 in the billing repository.", "text/plain", new("user", "golden-1"),
                "source", now.AddDays(-2), now.AddDays(-2));
            await store.AppendEpisodeAsync(source);
            var future = source with { Id = Guid.NewGuid(), Content = "Alice CSM-42 FUTURE", OccurredAt = now.AddDays(1) };
            var expired = source with { Id = Guid.NewGuid(), Content = "Alice CSM-42 EXPIRED", ExpiresAt = now };
            await store.AppendEpisodeAsync(future);
            await store.AppendEpisodeAsync(expired);
            for (var index = 0; index < 24; index++)
                await store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(), Content = $"Unrelated inventory stockroom item {index}" });
            var alice = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now)
                { Sensitivity = MemorySensitivity.Internal };
            var db = alice with { Id = Guid.NewGuid(), CanonicalName = "PostgreSQL", Type = "technology" };
            await store.UpsertEntityAsync(alice);
            await store.UpsertEntityAsync(db);
            var claim = new MemoryClaim(Guid.NewGuid(), partition, source.Id, alice.Id, "selected", db.Id, null,
                MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, 1, 1,
                now.AddDays(-1), now.AddDays(1), now);
            await store.WriteClaimAsync(claim);
            await store.WriteClaimAsync(claim with { Id = Guid.NewGuid(), Confirmation = MemoryConfirmationState.Rejected });
            await store.WriteClaimAsync(claim with { Id = Guid.NewGuid(), ValidFrom = now.AddDays(1) });
            var procedure = new ProceduralMemory(Guid.NewGuid(), partition, source.Id, "Restore service",
                "Restore the snapshot, then verify checksums.", "CSM-42 billing incident recovery", 1,
                MemoryTrustTier.ConfirmedUser, MemoryConfirmationState.Confirmed, now.AddDays(-1), now.AddDays(1), now);
            await store.WriteProcedureAsync(procedure);
            var scenarios = new[]
            {
                ("What did Alice choose for CSM-42?", MemoryLayer.Episodic, source.Id),
                ("Tell me about the billing repository decision", MemoryLayer.Episodic, source.Id),
                ("Which database did Alice select?", MemoryLayer.Semantic, claim.Id),
                ("Who selected PostgreSQL?", MemoryLayer.Semantic, claim.Id),
                ("How should I recover from the CSM-42 incident?", MemoryLayer.Procedural, procedure.Id)
            };
            foreach (var (query, layer, expected) in scenarios)
            {
                var watch = Stopwatch.StartNew();
                var found = await store.SearchAsync(new(partition, MemoryScope.Application, query, AsOf: now, Layers: new HashSet<MemoryLayer> { layer }));
                watch.Stop();
                Assert.Equal(expected, Assert.Single(found).Id);
                measurements.Add(new { query, layer = layer.ToString(), requiredRetrieved = true, forbiddenResults = 0,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, returnedCharacters = found.Sum(x => x.Content.Length) });
            }
            Assert.Empty(await store.SearchAsync(new(partition, MemoryScope.Application, "unicorn volcano", AsOf: now)));
            Assert.Empty(await store.SearchAsync(new(partition, MemoryScope.Application, "%%%", AsOf: now)));
            Assert.Empty(await store.SearchAsync(new(partition, MemoryScope.Application, "snapshot", AsOf: now.AddDays(1),
                Layers: new HashSet<MemoryLayer> { MemoryLayer.Procedural })));
            await store.WriteProcedureAsync(procedure with { Id = Guid.NewGuid(), Name = "Unrelated title", Procedure = "Restore service" });
            Assert.Equal(procedure.Id, Assert.Single(await store.SearchAsync(new(partition, MemoryScope.Application,
                "Restore service", Limit: 1, AsOf: now, Layers: new HashSet<MemoryLayer> { MemoryLayer.Procedural }))).Id);
            if (Environment.GetEnvironmentVariable("CSWEET_MEMORY_EVAL_OUTPUT") is { Length: > 0 } reportDirectory)
            {
                Directory.CreateDirectory(reportDirectory);
                await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"lexical-{provider}.json"), JsonSerializer.Serialize(
                    new { dataset = "memory-lexical-v1", provider, episodes = 27, claims = 3, procedures = 1, measurements },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            // The PostgreSQL caller-owned transaction must not execute nested readers per result.
            if (provider == "postgres")
            {
                await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES"));
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using var enlisted = new PostgreSqlMemoryStore(transaction);
                Assert.Contains(await enlisted.SearchAsync(new(partition, MemoryScope.Application, "Alice recover snapshot", AsOf: now)), x => x.Id == claim.Id);
            }
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            await store.DisposeAsync();
            if (provider == "sqlite")
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }
}
