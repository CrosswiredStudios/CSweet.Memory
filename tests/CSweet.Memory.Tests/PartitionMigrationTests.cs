using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Tests;

public sealed class PartitionMigrationTests
{
    public static TheoryData<string> Providers => ProvenanceStoreTests.Providers;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CanonicalKeysPreservePositionsSeparatorsAndNulls()
    {
        var a = new MemoryPartition("tenant", "app", "employee");
        var b = new MemoryPartition("tenant", "app", UserId: "employee");
        var c = new MemoryPartition("tenant", "app/employee");
        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.Key, c.Key);
        Assert.Equal(3, new[] { a.StorageKey, b.StorageKey, c.StorageKey }.Distinct().Count());
        Assert.NotEqual(a.StorageKey, (a with { UserId = "" }).StorageKey);
        Assert.NotEqual(a.StorageKey, (a with { UserId = " " }).StorageKey);
        Assert.Equal(a.StorageKey, JsonSerializer.Deserialize<MemoryPartition>(JsonSerializer.Serialize(a))!.StorageKey);
        Assert.Equal(68, a.StorageKey.Length);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshStoreSeparatesAliasedFieldsAndIdempotencyKeys(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        await using var store = fixture.Store();
        var partition = new MemoryPartition("tenant", "app", "employee");
        var alias = partition with { AgentId = null, UserId = partition.AgentId };
        var a = Episode(partition, "Employee secret") with { IdempotencyKey = "same-key" };
        var b = Episode(alias, "User secret") with { IdempotencyKey = "same-key" };
        Assert.True((await store.AppendEpisodeAsync(a)).Created);
        Assert.True((await store.AppendEpisodeAsync(b)).Created);
        Assert.Equal(a.Id, Assert.Single((await store.ExportAsync(partition)).Episodes).Id);
        Assert.Equal(b.Id, Assert.Single((await store.ExportAsync(alias)).Episodes).Id);
        await store.DeleteScopeAsync(alias);
        Assert.Equal(a.Id, Assert.Single((await store.ExportAsync(partition)).Episodes).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UnclassifiedLegacyScopeStaysQuarantined(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        var partition = new MemoryPartition("tenant", "private-app", "owner");
        var missing = Episode(partition, "Unclassified evidence");
        var known = Episode(partition, "Known evidence");
        await using (var initial = fixture.Store())
        {
            await initial.AppendEpisodeAsync(missing);
            await initial.AppendEpisodeAsync(known);
        }
        await fixture.DowngradeToLegacy();
        await fixture.Sql($"UPDATE {fixture.Prefix}episodes SET payload={(fixture.Postgres ? "payload - 'sensitivity'" : "json_remove(payload,'$.sensitivity')")} WHERE CAST(id AS text)=@id",
            ("id", missing.Id.ToString("D")));
        await using var store = fixture.Store();
        var migration = (IMemoryPartitionMigration)store;
        var plan = await migration.InspectPartitionMigrationAsync();
        Assert.Equal(2, plan.Rows.Count);
        Assert.All(plan.Rows, row => Assert.Equal("Quarantine", row.Disposition));
        await migration.ApplyPartitionMigrationAsync(plan.Fingerprint);
        Assert.Empty((await store.ExportAsync(partition)).Episodes);
        Assert.Equal(2L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}episodes")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ReviewedPopulatedMigrationQuarantinesAmbiguityAndRecoversAtomicFailure(string provider)
    {
        await using var fixture = await Fixture.Create(provider);
        var shared = new MemoryPartition("tenant", "csweet", "employee");
        var privateScope = shared with { ApplicationId = "installation-private" };
        var collision = new MemoryPartition("tenant", "old", "owner");
        var alias = collision with { AgentId = null, UserId = collision.AgentId };
        var source = Episode(shared, "Shared evidence");
        var personal = Episode(privateScope, "Private evidence") with { Sensitivity = MemorySensitivity.Confidential, LegalHold = true };
        var ambiguous = Episode(collision, "Ambiguous evidence");
        var shifted = Episode(alias, "Shifted evidence");
        var entity = new MemoryEntity(Guid.NewGuid(), shared, "person", "Alice", [], null, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Sensitivity = MemorySensitivity.Personal };
        var claim = new MemoryClaim(Guid.NewGuid(), shared, source.Id, entity.Id, "prefers", null, "PostgreSQL",
            MemoryTrustTier.UnconfirmedUser, MemoryConfirmationState.NotRequired, MemorySensitivity.Personal, 1, 1,
            source.OccurredAt, null, source.RecordedAt);
        var target = EmployeeMemoryNamespaces.Employee("tenant", "replacement", "csweet");
        var transfer = new KnowledgeTransferPackage(Guid.NewGuid(), "tenant", "employee", "replacement", [], target,
            "Legacy approved private debrief", [], MemorySensitivity.Personal, KnowledgeTransferStatus.Approved,
            DateTimeOffset.UtcNow, "reviewer");
        await using (var initial = fixture.Store())
        {
            foreach (var episode in new[] { source, personal, ambiguous, shifted }) await initial.AppendEpisodeAsync(episode);
            await initial.UpsertEntityAsync(entity);
            await initial.WriteClaimAsync(claim);
            await ((IKnowledgeTransferStore)initial).WriteKnowledgeTransferAsync(transfer);
        }
        await fixture.DowngradeToLegacy();
        await using var migrated = fixture.Store();
        Assert.Equal("memory_partition_migration_required", (await Assert.ThrowsAsync<InvalidOperationException>(() => migrated.InitializeAsync())).Message);
        var migration = (IMemoryPartitionMigration)migrated;
        var plan = await migration.InspectPartitionMigrationAsync();
        Assert.Equal(3, plan.Rows.Count(row => row.Disposition == "Quarantine"));
        Assert.Equal(privateScope.StorageKey, Assert.Single(plan.Rows, row => row.Id == personal.Id).DestinationKey);
        Assert.DoesNotContain("Shared evidence", JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("private debrief", JsonSerializer.Serialize(plan));
        // A changed row makes the reviewed fingerprint stale; no row may move.
        await fixture.Sql($"UPDATE {fixture.Prefix}episodes SET payload=REPLACE(CAST(payload AS text),'Shared evidence','Changed evidence'){(fixture.Postgres ? "::jsonb" : "")} WHERE CAST(id AS text)=@id", ("id", source.Id.ToString("D")));
        Assert.Equal("memory_migration_plan_changed", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => migration.ApplyPartitionMigrationAsync(plan.Fingerprint))).Message);
        plan = await migration.InspectPartitionMigrationAsync();
        await fixture.RejectClaimUpdate(true);
        await Assert.ThrowsAnyAsync<Exception>(() => migration.ApplyPartitionMigrationAsync(plan.Fingerprint));
        Assert.Equal(shared.Key, await fixture.Scalar($"SELECT partition_key FROM {fixture.Prefix}episodes WHERE CAST(id AS text)=@id", ("id", source.Id.ToString("D"))));
        Assert.Equal(0L, Convert.ToInt64(await fixture.Scalar($"SELECT count(*) FROM {fixture.Prefix}partition_migration_rows")));
        await fixture.RejectClaimUpdate(false);
        await migration.ApplyPartitionMigrationAsync(plan.Fingerprint);
        await migration.ApplyPartitionMigrationAsync(plan.Fingerprint);
        Assert.Equal(plan.Fingerprint, (await migration.InspectPartitionMigrationAsync()).Fingerprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migration.ApplyPartitionMigrationAsync("wrong-review"));
        await using var restarted = fixture.Store();
        await restarted.InitializeAsync();
        Assert.Equal(source.Id, Assert.Single((await restarted.ExportAsync(shared)).Episodes).Id);
        var preserved = Assert.Single((await restarted.ExportAsync(privateScope)).Episodes);
        Assert.Equal(personal.Id, preserved.Id);
        Assert.Equal(MemorySensitivity.Confidential, preserved.Sensitivity);
        Assert.True(preserved.LegalHold);
        Assert.Equal(claim.Id, Assert.Single((await restarted.ExportAsync(shared)).Claims).Id);
        Assert.Empty((await restarted.ExportAsync(collision)).Episodes);
        Assert.Empty((await restarted.ExportAsync(alias)).Episodes);
        Assert.Empty((await ((IMemoryRevisionReader)restarted).ReadRevisionsAsync(collision, MemoryRecordKind.Episode, ambiguous.Id)).Items);
        Assert.Empty((await ((IMemoryRevisionReader)restarted).ReadRevisionsAsync(alias, MemoryRecordKind.Episode, shifted.Id)).Items);
        Assert.Null(await ((IKnowledgeTransferStore)restarted).GetKnowledgeTransferAsync(transfer.Id));
        // Older direct writers cannot restore flattened keys or erase the new payload identity.
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Sql($"UPDATE {fixture.Prefix}episodes SET partition_key=@key WHERE CAST(id AS text)=@id",
            ("key", shared.Key), ("id", source.Id.ToString("D"))));
        await Assert.ThrowsAnyAsync<Exception>(() => restarted.AppendEpisodeAsync(ambiguous));
        await Assert.ThrowsAnyAsync<Exception>(() => ((IKnowledgeTransferStore)restarted).WriteKnowledgeTransferAsync(transfer));
        // Old wire JSON remains usable through a current server/store, which recomputes identity.
        var wire = JsonNode.Parse(JsonSerializer.Serialize(Episode(shared, "Old client payload"), JsonOptions))!;
        wire["partition"]!.AsObject().Remove("storageKey");
        var adapted = wire.Deserialize<MemoryEpisode>(JsonOptions)!;
        Assert.True((await restarted.AppendEpisodeAsync(adapted)).Created);
    }

    private static MemoryEpisode Episode(MemoryPartition partition, string content) => new(Guid.NewGuid(), partition,
        MemoryScope.Agent, content, "text/plain", new("user", "fixture"), "checksum", DateTimeOffset.UtcNow.AddDays(-1),
        DateTimeOffset.UtcNow, Sensitivity: MemorySensitivity.Personal);

    private sealed class Fixture(string provider, string connectionString, string path, string schema) : IAsyncDisposable
    {
        public bool Postgres => provider == "postgres";
        public string Prefix => Postgres ? "csweet_memory_" : "memory_";
        private static readonly string[] Kinds = ["episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings", "uses", "transfers"];
        public IMemoryStore Store() => Postgres ? new PostgreSqlMemoryStore(connectionString) : new SqliteMemoryStore(path);
        private DbConnection Connection() => Postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection(connectionString);
        public static async Task<Fixture> Create(string provider)
        {
            var path = Path.Combine(Path.GetTempPath(), $"memory-partition-{Guid.NewGuid():N}.db");
            var schema = $"memory_partition_{Guid.NewGuid():N}";
            var connection = provider == "postgres"
                ? new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")) { SearchPath = schema, Pooling = false }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
            var fixture = new Fixture(provider, connection, path, schema);
            if (fixture.Postgres) await fixture.Sql($"CREATE SCHEMA {schema}");
            return fixture;
        }
        public async Task<object?> Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = Connection(); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
            }
            return await command.ExecuteScalarAsync();
        }
        public async Task Sql(string sql, params (string Name, object Value)[] parameters) => await Scalar(sql, parameters);
        public async Task DowngradeToLegacy()
        {
            foreach (var kind in Kinds)
            {
                var table = Prefix + kind;
                await Sql(Postgres ? $"DROP TRIGGER {table}_canonical_guard ON {table}; DROP FUNCTION {table}_canonical_guard();"
                    : $"DROP TRIGGER {table}_canonical_insert; DROP TRIGGER {table}_canonical_update;");
                if (kind == "transfers") continue;
                var records = new List<(string Id, string Payload)>();
                await using (var connection = Connection())
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand(); command.CommandText = $"SELECT CAST(id AS text),CAST(payload AS text) FROM {table}";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) records.Add((reader.GetString(0), reader.GetString(1)));
                }
                foreach (var (id, payload) in records)
                {
                    var json = JsonNode.Parse(payload)!;
                    var partition = json["partition"]!.Deserialize<MemoryPartition>(JsonOptions)!;
                    json["partition"]!.AsObject().Remove("storageKey");
                    await Sql($"UPDATE {table} SET partition_key=@key,payload={(Postgres ? "CAST(@payload AS jsonb)" : "@payload")} WHERE CAST(id AS text)=@id",
                        ("key", partition.Key), ("payload", json.ToJsonString()), ("id", id));
                }
            }
            await Sql($"DELETE FROM {Prefix}schema_migrations WHERE id='canonical-partitions-v2'; DELETE FROM {Prefix}partition_migration_runs; DELETE FROM {Prefix}partition_migration_rows;");
        }
        public Task RejectClaimUpdate(bool enabled) => Sql(Postgres
            ? enabled ? $"CREATE FUNCTION reject_migration() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected migration failure'; END $$; CREATE TRIGGER reject_migration BEFORE UPDATE ON {Prefix}claims FOR EACH ROW EXECUTE FUNCTION reject_migration();"
                : $"DROP TRIGGER reject_migration ON {Prefix}claims; DROP FUNCTION reject_migration();"
            : enabled ? $"CREATE TRIGGER reject_migration BEFORE UPDATE ON {Prefix}claims BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;"
                : "DROP TRIGGER reject_migration;");
        public async ValueTask DisposeAsync()
        {
            if (Postgres) await Sql($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            else foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
