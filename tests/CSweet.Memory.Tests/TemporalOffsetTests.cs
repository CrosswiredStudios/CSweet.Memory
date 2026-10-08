using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CSweet.Memory.Tests;

public sealed class TemporalOffsetTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AllChannelsUseInstantsAndExclusiveEndAcrossOffsets(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-offset-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "offset-contract");
        try
        {
            var (episode, claim) = await SeedAsync(store, partition);
            foreach (var offset in new[] { -12, 0, 14 })
            {
                async Task<IReadOnlyList<MemoryCandidate>> Search(DateTimeOffset instant) => await store.SearchAsync(
                    new(partition, MemoryScope.Application, "Meridian", AsOf: instant.ToOffset(TimeSpan.FromHours(offset)), Embedding: [1f, 0f]));
                Assert.Empty(await Search(Start.AddTicks(-1)));
                foreach (var instant in new[] { Start, Start.AddHours(1).AddTicks(-1) })
                {
                    var results = await Search(instant);
                    foreach (var channel in new[] { "fulltext", "semantic", "graph", "procedure", "core", "vector" })
                        Assert.Contains(results, x => x.RetrievalChannel == channel);
                    Assert.Contains(results, x => x.Id == episode.Id);
                }
                Assert.Empty(await Search(Start.AddHours(1)));
                Assert.Empty(await Search(Start.AddHours(2)));
            }
            await store.SupersedeClaimAsync(claim.Id, Guid.NewGuid(), Start.AddMinutes(30).ToOffset(TimeSpan.FromHours(-12)));
            var before = await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start.AddMinutes(30).AddTicks(-1)));
            Assert.Contains(before, x => x.Id == claim.Id);
            var after = await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start.AddMinutes(30).ToOffset(TimeSpan.FromHours(14))));
            Assert.DoesNotContain(after, x => x.Id == claim.Id);
            var historical = await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start));
            Assert.Contains(historical, x => x.Id == claim.Id);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            await store.DisposeAsync();
            if (provider == "sqlite") File.Delete(path);
        }
    }

    [Fact]
    public async Task ExistingOffsetRowsAndLaterLegacyWritesWorkWithoutRewritingEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-offset-legacy-{Guid.NewGuid():N}.db");
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "offset-contract");
        try
        {
            await using (var original = new SqliteMemoryStore(path)) await SeedAsync(original, partition);
            await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            await raw.OpenAsync();
            async Task RestoreLegacyColumns()
            {
                await using var write = raw.CreateCommand();
                write.CommandText = """
                    UPDATE memory_episodes SET occurred_at=$start,expires_at=$end;
                    UPDATE memory_claims SET valid_from=$start,valid_to=$end;
                    UPDATE memory_edges SET valid_from=$start,valid_to=$end;
                    UPDATE memory_procedures SET valid_from=$start,valid_to=$end;
                    """;
                write.Parameters.AddWithValue("$start", Start.ToOffset(TimeSpan.FromHours(14)).ToString("O", CultureInfo.InvariantCulture));
                write.Parameters.AddWithValue("$end", Start.AddHours(1).ToOffset(TimeSpan.FromHours(-12)).ToString("O", CultureInfo.InvariantCulture));
                await write.ExecuteNonQueryAsync();
            }
            await RestoreLegacyColumns();
            await using var read = raw.CreateCommand();
            read.CommandText = "SELECT payload FROM memory_episodes LIMIT 1";
            var payload = (string)(await read.ExecuteScalarAsync())!;
            for (var reopen = 0; reopen < 2; reopen++)
            {
                await using var upgraded = new SqliteMemoryStore(path);
                var results = await upgraded.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start, Embedding: [1f, 0f]));
                foreach (var channel in new[] { "fulltext", "semantic", "graph", "procedure", "core", "vector" })
                    Assert.Contains(results, x => x.RetrievalChannel == channel);
                Assert.Equal(payload, await read.ExecuteScalarAsync());
                Assert.Empty(await upgraded.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start.AddHours(1), Embedding: [1f, 0f])));
                await RestoreLegacyColumns();
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026-10-06T12:00:00")]
    public async Task InvalidLegacyTimeDoesNotBecomeAnOpenEndedInterval(string invalid)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-offset-invalid-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteMemoryStore(path);
            var partition = new MemoryPartition(Guid.NewGuid().ToString(), "offset-contract");
            await SeedAsync(store, partition);
            await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            await raw.OpenAsync();
            await using var write = raw.CreateCommand();
            write.CommandText = "UPDATE memory_episodes SET expires_at=$invalid";
            write.Parameters.AddWithValue("$invalid", invalid);
            await write.ExecuteNonQueryAsync();
            Assert.Empty(await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", AsOf: Start,
                Embedding: [1f, 0f], Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic, MemoryLayer.Semantic, MemoryLayer.Procedural })));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExpiredOffsetRowsCannotConsumeTheLexicalResultLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-offset-limit-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteMemoryStore(path);
            var partition = new MemoryPartition(Guid.NewGuid().ToString(), "offset-contract");
            var expired = new MemoryEpisode(Guid.Parse("00000000-0000-0000-0000-000000000001"), partition, MemoryScope.Application,
                "Meridian evidence", "text/plain", new("user", "fixture"), "checksum", Start.AddHours(-2), Start,
                ExpiresAt: Start.AddHours(-1).ToOffset(TimeSpan.FromHours(14)), Sensitivity: MemorySensitivity.Internal);
            var eligible = expired with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), ExpiresAt = Start.AddHours(1) };
            await store.AppendEpisodeAsync(expired);
            await store.AppendEpisodeAsync(eligible);
            await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
            await raw.OpenAsync();
            await using var command = raw.CreateCommand();
            command.CommandText = "UPDATE memory_episodes SET expires_at=$expires WHERE id=$id";
            command.Parameters.AddWithValue("$expires", expired.ExpiresAt!.Value.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", expired.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
            var results = await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", Limit: 1, AsOf: Start,
                Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic }));
            Assert.Equal(eligible.Id, Assert.Single(results).Id);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SubMicrosecondSourceBoundariesPreserveAllChannels(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-ticks-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        try
        {
            // Includes dates before PostgreSQL's epoch and a fraction that can round across a second.
            foreach (var year in new[] { 1, 1900, 2000, 2026, 9999 })
            foreach (var fraction in new[] { 1L, 7L, 9_999_999L })
            {
                var start = new DateTimeOffset(year, 10, 6, 12, 0, 0, TimeSpan.Zero).AddTicks(fraction);
                var partition = new MemoryPartition(Guid.NewGuid().ToString(), "tick-contract");
                try
                {
                    await SeedAsync(store, partition, start);
                    async Task<IReadOnlyList<MemoryCandidate>> Search(DateTimeOffset instant) => await store.SearchAsync(
                        new(partition, MemoryScope.Application, "Meridian", AsOf: instant.ToOffset(TimeSpan.FromHours(-12)), Embedding: [1f, 0f]));
                    Assert.Empty(await Search(start.AddTicks(-1)));
                    foreach (var instant in new[] { start, start.AddHours(1).AddTicks(-1) })
                    {
                        var results = await Search(instant);
                        foreach (var channel in new[] { "fulltext", "semantic", "graph", "procedure", "core", "vector" })
                            Assert.Contains(results, x => x.RetrievalChannel == channel);
                    }
                    Assert.Empty(await Search(start.AddHours(1)));
                }
                finally { await store.DeleteScopeAsync(partition); }
            }
        }
        finally { await store.DisposeAsync(); if (provider == "sqlite") File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SubMicrosecondDerivativeIntervalsAreIndependentOfSourceValidity(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-derivative-ticks-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        var partition = new MemoryPartition(Guid.NewGuid().ToString(), "tick-contract");
        var from = Start.AddTicks(7);
        var to = Start.AddMinutes(30).AddTicks(9);
        try
        {
            var (_, claim) = await SeedAsync(store, partition, Start.AddMinutes(-1), from, to);
            async Task<IReadOnlyList<MemoryCandidate>> Search(DateTimeOffset instant) => await store.SearchAsync(new(
                partition, MemoryScope.Application, "Meridian", AsOf: instant,
                Layers: new HashSet<MemoryLayer> { MemoryLayer.Semantic, MemoryLayer.Procedural }));
            Assert.Empty(await Search(from.AddTicks(-1)));
            foreach (var instant in new[] { from, to.AddTicks(-1) })
            {
                var results = await Search(instant);
                foreach (var channel in new[] { "semantic", "graph", "procedure" })
                    Assert.Contains(results, x => x.RetrievalChannel == channel);
            }
            Assert.Empty(await Search(to));
            var cutoff = Start.AddMinutes(15).AddTicks(3).ToOffset(TimeSpan.FromHours(14));
            await store.SupersedeClaimAsync(claim.Id, Guid.NewGuid(), cutoff);
            Assert.Contains(await Search(cutoff.AddTicks(-1)), x => x.Id == claim.Id);
            Assert.DoesNotContain(await Search(cutoff), x => x.Id == claim.Id);
        }
        finally
        {
            await store.DeleteScopeAsync(partition);
            await store.DisposeAsync();
            if (provider == "sqlite") File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SubMicrosecondCandidatesAreFilteredBeforeLimits(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"memory-tick-limit-{Guid.NewGuid():N}.db");
        await using IMemoryStore store = provider == "postgres"
            ? new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!)
            : new SqliteMemoryStore(path);
        try
        {
            foreach (var layer in new[] { MemoryLayer.Episodic, MemoryLayer.Semantic, MemoryLayer.Procedural })
            {
                var partition = new MemoryPartition(Guid.NewGuid().ToString(), "tick-limit-contract");
                try
                {
                    var source = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "evidence", "text/plain",
                        new("user", "fixture"), "checksum", Start.AddDays(-1), Start, Sensitivity: MemorySensitivity.Internal);
                    await store.AppendEpisodeAsync(source);
                    var entity = new MemoryEntity(Guid.NewGuid(), partition, "topic", "Meridian", [], null, false, Start, Start)
                        { Sensitivity = MemorySensitivity.Internal };
                    await store.UpsertEntityAsync(entity);
                    var eligibleId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
                    // Ties rank by ID. Neither the future nor expired row may take the one available slot.
                    foreach (var (id, begin, end) in new[]
                    {
                        (Guid.Parse("00000000-0000-0000-0000-000000000001"), Start.AddTicks(7), Start.AddHours(1)),
                        (Guid.Parse("00000000-0000-0000-0000-000000000002"), Start.AddHours(-1), Start.AddTicks(5)),
                        (eligibleId, Start.AddHours(-1), Start.AddHours(1))
                    })
                    {
                        switch (layer)
                        {
                            case MemoryLayer.Episodic:
                                await store.AppendEpisodeAsync(source with { Id = id, Content = "Meridian", OccurredAt = begin, ExpiresAt = end });
                                break;
                            case MemoryLayer.Semantic:
                                await store.WriteClaimAsync(new(id, partition, source.Id, entity.Id, "uses", null, "Meridian",
                                    MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal,
                                    1, 1, begin, end, Start));
                                break;
                            case MemoryLayer.Procedural:
                                await store.WriteProcedureAsync(new(id, partition, source.Id, "Meridian", "Meridian", "Meridian", 1,
                                    MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.Confirmed, begin, end, Start));
                                break;
                        }
                    }
                    var results = await store.SearchAsync(new(partition, MemoryScope.Application, "Meridian", Limit: 1,
                        AsOf: Start.AddTicks(6), Layers: new HashSet<MemoryLayer> { layer }));
                    Assert.Equal(eligibleId, Assert.Single(results).Id);
                }
                finally { await store.DeleteScopeAsync(partition); }
            }
        }
        finally { await store.DisposeAsync(); if (provider == "sqlite") File.Delete(path); }
    }

    private static async Task<(MemoryEpisode, MemoryClaim)> SeedAsync(IMemoryStore store, MemoryPartition partition,
        DateTimeOffset? start = null, DateTimeOffset? validFrom = null, DateTimeOffset? validTo = null)
    {
        var begin = (start ?? Start).ToOffset(TimeSpan.FromHours(14));
        var end = begin.AddHours(1).ToOffset(TimeSpan.FromHours(-12));
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Application, "Meridian evidence", "text/plain",
            new("user", "fixture"), "checksum", begin, Start.AddDays(2), ExpiresAt: end, Sensitivity: MemorySensitivity.Internal);
        await store.AppendEpisodeAsync(episode);
        var subject = new MemoryEntity(Guid.NewGuid(), partition, "topic", "Meridian", [], null, false, begin, begin)
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [episode.Id] };
        var target = subject with { Id = Guid.NewGuid(), CanonicalName = "Destination" };
        await store.UpsertEntityAsync(subject);
        await store.UpsertEntityAsync(target);
        var claim = new MemoryClaim(Guid.NewGuid(), partition, episode.Id, subject.Id, "uses", null, "Meridian value",
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Internal, 1, 1, validFrom ?? begin, validTo ?? end, Start.AddDays(2));
        await store.WriteClaimAsync(claim);
        await store.WriteEdgeAsync(new(Guid.NewGuid(), partition, episode.Id, subject.Id, "links", target.Id,
            MemoryTrustTier.UnconfirmedUser, 1, validFrom ?? begin, validTo ?? end, false, Start.AddDays(2)));
        await store.WriteProcedureAsync(new(Guid.NewGuid(), partition, episode.Id, "Meridian procedure", "Follow Meridian evidence", "Meridian",
            1, MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.Confirmed, validFrom ?? begin, validTo ?? end, Start.AddDays(2)));
        await store.WriteBlockAsync(new(Guid.NewGuid(), partition, "Meridian", "Meridian summary", 1, 100, true, MemoryTrustTier.UnconfirmedUser, begin)
            { SourceEpisodeIds = [episode.Id], Sensitivity = MemorySensitivity.Internal, Confirmation = MemoryConfirmationState.Confirmed });
        await store.WriteEmbeddingAsync(new(Guid.NewGuid(), partition, episode.Id, MemoryLayer.Episodic, [1f, 0f], "test", Start.AddDays(2)));
        return (episode, claim);
    }
}
