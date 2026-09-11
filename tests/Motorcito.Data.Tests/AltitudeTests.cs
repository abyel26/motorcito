using Motorcito.Data;
using Motorcito.Obd;

namespace Motorcito.Data.Tests;

/// <summary>An altitude source the test controls, standing in for a GPS fix.</summary>
internal sealed class FakeAltitudeProvider : IAltitudeProvider
{
    public double? LastKnownAltitudeM { get; set; }
}

public class AltitudeTests
{
    private const string User = LoggingService.LocalUserId;
    private const string Car = "car-1";

    private static (MotorcitoDatabase db, TripRecorder rec) NewRecorder(IAltitudeProvider? altitude)
    {
        var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var rec = new TripRecorder(new TripRepository(db), new SampleRepository(db),
            User, Car, new TripRecorderOptions { BatchSize = 1 }, altitude);
        return (db, rec);
    }

    private static ObdSnapshot Running(DateTime ts, double rpm = 800) => new(ts,
        new Dictionary<byte, PidReading> { [0x0C] = new(PidRegistry.Find(0x0C)!, rpm, []) }, 1);

    [Fact]
    public void Altitude_is_recorded_at_trip_start()
    {
        var (db, rec) = NewRecorder(new FakeAltitudeProvider { LastKnownAltitudeM = 667 });
        using var _ = db;

        rec.Record(Running(DateTime.UtcNow));

        // Madrid sits at roughly 667 m; the same engine trims differently there
        // than at sea level, so this is what stops a mountain drive reading as
        // a developing fault.
        Assert.Equal(667, new TripRepository(db).ForVehicle(Car).Single().StartAltitudeM);
    }

    [Fact]
    public void A_trip_without_altitude_still_records()
    {
        var (db, rec) = NewRecorder(new FakeAltitudeProvider { LastKnownAltitudeM = null });
        using var _ = db;

        rec.Record(Running(DateTime.UtcNow));

        // Permission refused must degrade logging, never prevent it.
        var trip = new TripRepository(db).ForVehicle(Car).Single();
        Assert.Null(trip.StartAltitudeM);
        Assert.NotNull(trip.TripId);
    }

    [Fact]
    public void Null_means_not_measured_rather_than_sea_level()
    {
        using var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);

        // One trip with no fix, one measured at sea level.
        var unknown = new FakeAltitudeProvider { LastKnownAltitudeM = null };
        var seaLevel = new FakeAltitudeProvider { LastKnownAltitudeM = 0 };

        new TripRecorder(trips, samples, User, Car, new TripRecorderOptions { BatchSize = 1 }, unknown)
            .Record(Running(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)));
        new TripRecorder(trips, samples, User, Car, new TripRecorderOptions { BatchSize = 1 }, seaLevel)
            .Record(Running(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));

        var recorded = trips.ForVehicle(Car).OrderBy(t => t.StartedAt).ToList();

        // Storing 0 for an unknown fix would band an unmeasured trip as coastal
        // and quietly corrupt any altitude-normalised baseline.
        Assert.Null(recorded[0].StartAltitudeM);
        Assert.Equal(0, recorded[1].StartAltitudeM);
    }

    [Fact]
    public void Each_trip_captures_the_altitude_current_when_it_started()
    {
        var altitude = new FakeAltitudeProvider { LastKnownAltitudeM = 100 };
        using var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });

        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);
        var options = new TripRecorderOptions { BatchSize = 1, IdleBeforeTripEnd = TimeSpan.FromSeconds(30) };
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var first = new TripRecorder(trips, samples, User, Car, options, altitude);
        first.Record(Running(t0));
        first.OnDisconnected();

        // Drove up a mountain between trips.
        altitude.LastKnownAltitudeM = 1800;

        var second = new TripRecorder(trips, samples, User, Car, options, altitude);
        second.Record(Running(t0.AddHours(2)));
        second.OnDisconnected();

        var recorded = trips.ForVehicle(Car).OrderBy(t => t.StartedAt).ToList();
        Assert.Equal(100, recorded[0].StartAltitudeM);
        Assert.Equal(1800, recorded[1].StartAltitudeM);
    }

    [Fact]
    public void Recording_works_with_no_altitude_provider_at_all()
    {
        // The default: an app built without location support must still log.
        var (db, rec) = NewRecorder(null);
        using var _ = db;

        rec.Record(Running(DateTime.UtcNow));

        Assert.Null(new TripRepository(db).ForVehicle(Car).Single().StartAltitudeM);
    }

    [Fact]
    public void Altitude_survives_a_round_trip_through_the_database()
    {
        var (db, rec) = NewRecorder(new FakeAltitudeProvider { LastKnownAltitudeM = -3.5 });
        using var _ = db;

        rec.Record(Running(DateTime.UtcNow));
        var tripId = rec.CurrentTripId!;

        // Below sea level is legitimate — Death Valley, parts of the
        // Netherlands — so negative values must not be treated as invalid.
        Assert.Equal(-3.5, new TripRepository(db).Get(tripId)!.StartAltitudeM);
    }

    [Fact]
    public void The_schema_holds_no_coordinate_columns()
    {
        // The privacy rule, asserted structurally: altitude is all the
        // normalisation needs, and there is nowhere in this database for a
        // latitude or longitude to be stored even by mistake.
        using var db = new MotorcitoDatabase(":memory:");

        using var tables = db.CreateCommand("SELECT name FROM sqlite_master WHERE type='table';");
        var names = new List<string>();
        using (var reader = tables.ExecuteReader())
        {
            while (reader.Read())
                names.Add(reader.GetString(0));
        }

        foreach (var table in names)
        {
            using var info = db.CreateCommand($"PRAGMA table_info({table});");
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var column = reader.GetString(1).ToLowerInvariant();
                Assert.DoesNotContain("latitude", column);
                Assert.DoesNotContain("longitude", column);
                Assert.False(column is "lat" or "lon" or "lng", $"{table}.{column} looks like a coordinate");
            }
        }
    }
}
