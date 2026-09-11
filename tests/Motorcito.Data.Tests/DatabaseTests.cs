using Motorcito.Data;

namespace Motorcito.Data.Tests;

public class DatabaseTests
{
    [Fact]
    public void Applies_migrations_on_creation()
    {
        using var db = new MotorcitoDatabase(":memory:");

        Assert.Equal(Migrations.LatestVersion, db.SchemaVersion);
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        using var db = new MotorcitoDatabase(":memory:");

        // Re-applying must be a no-op, not an error — this runs on every start.
        var version = Migrations.Apply(db.Connection);

        Assert.Equal(Migrations.LatestVersion, version);
    }

    [Fact]
    public void Creates_every_table_from_the_schema_doc()
    {
        using var db = new MotorcitoDatabase(":memory:");
        using var command = db.CreateCommand("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");

        var tables = new List<string>();
        using var r = command.ExecuteReader();
        while (r.Read())
            tables.Add(r.GetString(0));

        foreach (var expected in new[]
                 {
                     "baselines", "events", "maintenance_log", "samples",
                     "sync_queue", "trips", "vehicles"
                 })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void Every_table_carries_user_id_and_vehicle_id()
    {
        // Retrofitting multi-tenancy would be a rewrite. Retrofitting multi-tenancy is a rewrite, so this is
        // pinned rather than trusted. samples is the documented exception: it
        // carries vehicle_id and reaches user_id through its trip.
        using var db = new MotorcitoDatabase(":memory:");

        foreach (var table in new[] { "vehicles", "trips", "events", "baselines", "maintenance_log" })
        {
            var columns = Columns(db, table);
            Assert.Contains("user_id", columns);

            if (table != "vehicles")
                Assert.Contains("vehicle_id", columns);
        }

        Assert.Contains("vehicle_id", Columns(db, "samples"));
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var samples = new SampleRepository(db);

        // No such trip, so the insert must be rejected rather than orphaned.
        Assert.ThrowsAny<Exception>(() => samples.InsertBatch([
            new Sample { TripId = "nope", VehicleId = "v1", Timestamp = DateTime.UtcNow }
        ]));
    }

    private static List<string> Columns(MotorcitoDatabase db, string table)
    {
        using var command = db.CreateCommand($"PRAGMA table_info({table});");
        var names = new List<string>();
        using var r = command.ExecuteReader();
        while (r.Read())
            names.Add(r.GetString(1));
        return names;
    }
}
