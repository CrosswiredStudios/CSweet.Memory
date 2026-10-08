using System.Text.Json;
using CSweet.Memory;

if (args.Length != 3 || args[0] is not ("inspect" or "apply") || args[1] is not ("postgres" or "sqlite"))
{
    Console.Error.WriteLine("Usage: CSweet.Memory.Migrate <inspect|apply> <postgres|sqlite> <plan.json>");
    Console.Error.WriteLine("Set CSWEET_MEMORY_MIGRATION_CONNECTION to the PostgreSQL connection string or SQLite file path. Stop writers and back up before applying.");
    return 2;
}
var connection = Environment.GetEnvironmentVariable("CSWEET_MEMORY_MIGRATION_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("CSWEET_MEMORY_MIGRATION_CONNECTION is required.");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    await using IMemoryStore store = args[1] == "postgres" ? new PostgreSqlMemoryStore(connection) : new SqliteMemoryStore(connection);
    var migration = (IMemoryPartitionMigration)store;
    if (args[0] == "inspect")
    {
        var plan = await migration.InspectPartitionMigrationAsync(cancellation.Token);
        await using var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, plan, json, cancellation.Token);
        Console.WriteLine($"Review {plan.Rows.Count} records: {plan.Rows.Count(x => x.Disposition == "Quarantine")} quarantined. Fingerprint: {plan.Fingerprint}");
    }
    else
    {
        var reviewed = JsonSerializer.Deserialize<MemoryPartitionMigrationPlan>(await File.ReadAllTextAsync(args[2], cancellation.Token), json)
            ?? throw new InvalidOperationException("memory_migration_plan_invalid");
        var current = await migration.InspectPartitionMigrationAsync(cancellation.Token);
        if (reviewed.Version != MemoryPartitionMigrationPlan.CurrentVersion ||
            JsonSerializer.Serialize(reviewed, json) != JsonSerializer.Serialize(current, json))
            throw new InvalidOperationException("memory_migration_plan_changed");
        await migration.ApplyPartitionMigrationAsync(reviewed.Fingerprint, cancellation.Token);
        Console.WriteLine($"Migration applied or already applied. Fingerprint: {reviewed.Fingerprint}");
    }
    return 0;
}
catch (Exception error)
{
    // Never echo provider connection details, source payloads or SQL parameters.
    Console.Error.WriteLine(error is InvalidOperationException && error.Message.StartsWith("memory_", StringComparison.Ordinal)
        ? error.Message : $"Migration did not complete ({error.GetType().Name}). Retain the plan and inspect database state before retrying.");
    return 1;
}
