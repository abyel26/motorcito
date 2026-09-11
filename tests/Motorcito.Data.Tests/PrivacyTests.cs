using Motorcito.Data;
using Motorcito.Obd;

namespace Motorcito.Data.Tests;

public class DataErasureTests
{
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";

    /// <summary>Populates one user with data across every table that holds any.</summary>
    private static string Seed(MotorcitoDatabase db, string userId, string vin)
    {
        var vehicles = new VehicleRepository(db);
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);

        var vehicle = vehicles.ResolveOrCreate(userId, vin, "[4,5,12]");

        var recorder = new TripRecorder(trips, samples, userId, vehicle.VehicleId,
            new TripRecorderOptions { BatchSize = 1 });

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            recorder.Record(new ObdSnapshot(t0.AddSeconds(i * 10), new Dictionary<byte, PidReading>
            {
                [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, []),
                [0x0D] = new(PidRegistry.Find(0x0D)!, 60, [])
            }, 1));
        }
        recorder.OnDisconnected();

        // Rows in the tables with no repository yet, so erasure is tested
        // against the whole schema rather than only what is wired up today.
        using (var command = db.CreateCommand("""
            INSERT INTO events (event_id, vehicle_id, user_id, ts, event_type, code, severity, acknowledged, synced)
            VALUES ($e, $v, $u, $ts, 'Dtc', 'P0171', 'Warning', 0, 0);

            INSERT INTO baselines (baseline_id, vehicle_id, user_id, metric, coolant_band, load_band,
                                   rpm_band, speed_band, window_start, window_end, mean_value,
                                   sample_count, trip_count, is_sufficient)
            VALUES ($b, $v, $u, 'ltft1', 'warm', 'mid', '1500-2500', '50-90', $ts, $ts, 3.5, 600, 12, 1);

            INSERT INTO maintenance_log (entry_id, vehicle_id, user_id, performed_at, category, notes)
            VALUES ($m, $v, $u, $ts, 'oil', 'changed');

            INSERT INTO sync_queue (entity_type, entity_id, user_id)
            VALUES ('trip', $q, $u);

            INSERT INTO obd_log (user_id, vehicle_id, session_id, ts, command, raw_response, status, duration_ms)
            VALUES ($u, $v, 'session-1', $ts, '0902', '49 02 01 57 42 53', 'Data', 12);
            """))
        {
            command.Parameters.AddWithValue("$e", $"event-{userId}");
            command.Parameters.AddWithValue("$b", $"baseline-{userId}");
            command.Parameters.AddWithValue("$m", $"maint-{userId}");
            command.Parameters.AddWithValue("$q", $"queued-{userId}");
            command.Parameters.AddWithValue("$v", vehicle.VehicleId);
            command.Parameters.AddWithValue("$u", userId);
            command.Parameters.AddWithValue("$ts", Timestamps.ToDb(t0));
            command.ExecuteNonQuery();
        }

        return vehicle.VehicleId;
    }

    [Fact]
    public void Erasure_removes_every_row_for_the_user()
    {
        using var db = new MotorcitoDatabase(":memory:");
        Seed(db, Alice, "WBS8M9C50J5K12345");

        Assert.True(DataErasure.CountRowsForUser(db, Alice) > 0);

        var report = DataErasure.EraseUser(db, Alice);

        // The check that matters: nothing survives anywhere.
        Assert.Equal(0, DataErasure.CountRowsForUser(db, Alice));
        Assert.True(report.Samples > 0);
        Assert.Equal(1, report.Trips);
        Assert.Equal(1, report.Events);
        Assert.Equal(1, report.Baselines);
        Assert.Equal(1, report.MaintenanceEntries);
        Assert.Equal(1, report.SyncQueueEntries);
        Assert.Equal(1, report.ObdLogEntries);
        Assert.Equal(1, report.Vehicles);
    }

    [Fact]
    public void Erasure_does_not_touch_another_user()
    {
        using var db = new MotorcitoDatabase(":memory:");
        Seed(db, Alice, "WBS8M9C50J5K12345");
        Seed(db, Bob, "JM1NDAD75M0123456");

        var bobBefore = DataErasure.CountRowsForUser(db, Bob);
        DataErasure.EraseUser(db, Alice);

        Assert.Equal(0, DataErasure.CountRowsForUser(db, Alice));
        Assert.Equal(bobBefore, DataErasure.CountRowsForUser(db, Bob));
    }

    [Fact]
    public void Erasure_leaves_no_orphaned_samples()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicleId = Seed(db, Alice, "WBS8M9C50J5K12345");

        DataErasure.EraseUser(db, Alice);

        // Samples carry vehicle_id but not user_id, so they are the row most
        // likely to be left behind by a naive erasure.
        using var command = db.CreateCommand("SELECT COUNT(*) FROM samples WHERE vehicle_id = $v;");
        command.Parameters.AddWithValue("$v", vehicleId);
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void Erasure_is_idempotent()
    {
        using var db = new MotorcitoDatabase(":memory:");
        Seed(db, Alice, "WBS8M9C50J5K12345");

        DataErasure.EraseUser(db, Alice);
        var second = DataErasure.EraseUser(db, Alice);

        Assert.Equal(0, second.Total);
    }

    [Fact]
    public void Erasure_stops_an_active_recording_first()
    {
        using var logging = new LoggingService(":memory:");
        var supported = SupportedPids.FromPids([0x0C]);

        logging.StartRecording("WBS8M9C50J5K12345", supported, new TripRecorderOptions { BatchSize = 1 });
        logging.Record(new ObdSnapshot(DateTime.UtcNow, new Dictionary<byte, PidReading>
        {
            [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, [])
        }, 1));

        logging.EraseUser();

        // A live recorder would otherwise reinsert rows against a vehicle that
        // had just been deleted.
        Assert.Null(logging.Recorder);
        Assert.Equal(0, logging.CountRowsForUser());
    }
}

public class VinHasherTests
{
    private const string Pepper = "server-side-secret";

    [Fact]
    public void The_same_vin_always_hashes_the_same()
    {
        Assert.Equal(
            VinHasher.Hash("WBS8M9C50J5K12345", Pepper),
            VinHasher.Hash("WBS8M9C50J5K12345", Pepper));
    }

    [Fact]
    public void Casing_and_padding_do_not_change_the_hash()
    {
        // Adapters differ in echo formatting; a trailing space must not split
        // one car's history across two identities.
        Assert.Equal(
            VinHasher.Hash("WBS8M9C50J5K12345", Pepper),
            VinHasher.Hash("  wbs8m9c50j5k12345 ", Pepper));
    }

    [Fact]
    public void Different_vins_hash_differently()
    {
        Assert.NotEqual(
            VinHasher.Hash("WBS8M9C50J5K12345", Pepper),
            VinHasher.Hash("JM1NDAD75M0123456", Pepper));
    }

    [Fact]
    public void A_different_pepper_gives_a_different_hash()
    {
        // What makes the hash non-reversible in practice: the VIN space is
        // small enough to brute-force without a secret key.
        Assert.NotEqual(
            VinHasher.Hash("WBS8M9C50J5K12345", Pepper),
            VinHasher.Hash("WBS8M9C50J5K12345", "a-different-secret"));
    }

    [Fact]
    public void The_hash_does_not_contain_the_vin()
    {
        var hash = VinHasher.Hash("WBS8M9C50J5K12345", Pepper);

        Assert.DoesNotContain("WBS8M9C50J5K12345", hash);
        Assert.Equal(64, hash.Length);   // SHA-256, hex
    }

    [Fact]
    public void Rejects_an_empty_pepper()
    {
        // Hashing without a key would be trivially reversible, so this must
        // fail loudly rather than produce a useless identifier.
        Assert.Throws<ArgumentException>(() => VinHasher.Hash("WBS8M9C50J5K12345", ""));
    }
}

public class SchemaTests
{
    [Fact]
    public void Sync_queue_carries_user_id()
    {
        using var db = new MotorcitoDatabase(":memory:");
        using var command = db.CreateCommand("PRAGMA table_info(sync_queue);");

        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        // Every table carries user_id, and erasure depends on it.
        Assert.Contains("user_id", columns);
    }

    [Fact]
    public void Reopening_an_existing_database_preserves_its_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motorcito-migrate-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new MotorcitoDatabase(path))
            {
                using var insert = db.CreateCommand(
                    "INSERT INTO sync_queue (entity_type, entity_id, user_id) VALUES ('trip', 'abc', 'user-1');");
                insert.ExecuteNonQuery();
            }

            // Reopening re-runs migration detection on every startup;
            // already-applied scripts must be skipped, not reapplied.
            using var reopened = new MotorcitoDatabase(path);
            Assert.Equal(Migrations.LatestVersion, reopened.SchemaVersion);

            using var count = reopened.CreateCommand("SELECT COUNT(*) FROM sync_queue WHERE entity_id = 'abc';");
            Assert.Equal(1, Convert.ToInt32(count.ExecuteScalar()));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}*"))
                File.Delete(file);
        }
    }
}
