using Motorcito.Data;
using Motorcito.Obd;

namespace Motorcito.Data.Tests;

public class TripRecorderTests
{
    private const string User = "user-1";
    private const string Car = "car-1";

    private static (MotorcitoDatabase db, TripRecorder rec) NewRecorder(TripRecorderOptions? options = null)
    {
        var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var rec = new TripRecorder(new TripRepository(db), new SampleRepository(db), User, Car, options);
        return (db, rec);
    }

    /// <summary>Builds a snapshot with the given readings, keyed by PID.</summary>
    private static ObdSnapshot Snap(DateTime ts, params (byte Pid, double Value)[] values)
    {
        var readings = values.ToDictionary(
            v => v.Pid,
            v => new PidReading(PidRegistry.Find(v.Pid)!, v.Value, []));

        return new ObdSnapshot(ts, readings, 10);
    }

    [Fact]
    public void Does_not_open_a_trip_while_the_engine_is_off()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;

        // Ignition on, engine not running. Connecting should not create a trip.
        rec.Record(Snap(DateTime.UtcNow, (0x0C, 0), (0x05, 20)));

        Assert.Null(rec.CurrentTripId);
        Assert.Empty(new TripRepository(db).ForVehicle(Car));
    }

    [Fact]
    public void Starts_a_trip_when_the_engine_runs()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;

        rec.Record(Snap(DateTime.UtcNow, (0x0C, 800), (0x05, 20)));

        Assert.NotNull(rec.CurrentTripId);
        var trip = new TripRepository(db).ForVehicle(Car).Single();
        Assert.True(trip.WasColdStart);      // coolant 20 °C
        Assert.Null(trip.EndedAt);
    }

    [Fact]
    public void Marks_a_warm_start_as_not_cold()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;

        rec.Record(Snap(DateTime.UtcNow, (0x0C, 800), (0x05, 88)));

        Assert.False(new TripRepository(db).ForVehicle(Car).Single().WasColdStart);
    }

    [Fact]
    public void A_brief_stop_does_not_split_the_trip()
    {
        var (db, rec) = NewRecorder(new TripRecorderOptions { IdleBeforeTripEnd = TimeSpan.FromSeconds(30) });
        using var _ = db;
        var t0 = DateTime.UtcNow;

        rec.Record(Snap(t0, (0x0C, 800)));
        // Stalled or stop-start at a light, 10 s — well under the threshold.
        rec.Record(Snap(t0.AddSeconds(5), (0x0C, 0)));
        rec.Record(Snap(t0.AddSeconds(10), (0x0C, 0)));
        rec.Record(Snap(t0.AddSeconds(15), (0x0C, 900)));

        Assert.NotNull(rec.CurrentTripId);
        Assert.Single(new TripRepository(db).ForVehicle(Car));
    }

    [Fact]
    public void Ends_the_trip_after_sustained_zero_rpm()
    {
        var (db, rec) = NewRecorder(new TripRecorderOptions { IdleBeforeTripEnd = TimeSpan.FromSeconds(30) });
        using var _ = db;
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        rec.Record(Snap(t0, (0x0C, 800)));
        rec.Record(Snap(t0.AddSeconds(10), (0x0C, 0)));   // engine stops here
        rec.Record(Snap(t0.AddSeconds(45), (0x0C, 0)));   // 35 s later we are sure

        Assert.Null(rec.CurrentTripId);
        var trip = new TripRepository(db).ForVehicle(Car).Single();

        // Exact, not ">=". The end time is when the engine stopped (t+10), not
        // when the wait expired (t+45). A loose assertion here previously
        // accepted a 30-second inflation on every trip.
        Assert.Equal(t0.AddSeconds(10), trip.EndedAt);
        Assert.Equal(10, trip.DurationS);
    }

    [Fact]
    public void End_time_does_not_depend_on_how_long_we_waited()
    {
        // The whole point of backdating: a longer timeout must not change the
        // recorded trip, only how confident we are before writing it.
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var durations = new List<int?>();

        foreach (var timeout in new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3) })
        {
            var (db, rec) = NewRecorder(new TripRecorderOptions { IdleBeforeTripEnd = timeout });
            using var _ = db;

            rec.Record(Snap(t0, (0x0C, 800)));
            rec.Record(Snap(t0.AddSeconds(10), (0x0C, 0)));
            rec.Record(Snap(t0.AddMinutes(10), (0x0C, 0)));   // well past either timeout

            durations.Add(new TripRepository(db).ForVehicle(Car).Single().DurationS);
        }

        Assert.Equal(durations[0], durations[1]);
        Assert.Equal(10, durations[0]);
    }

    [Fact]
    public void A_three_minute_stop_start_event_does_not_split_the_trip()
    {
        // Auto start-stop at a long red light: engine genuinely off, RPM zero.
        var (db, rec) = NewRecorder();   // default 3-minute timeout
        using var _ = db;
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        rec.Record(Snap(t0, (0x0C, 800)));
        for (var s = 5; s <= 90; s += 5)                       // 90 s stopped
            rec.Record(Snap(t0.AddSeconds(s), (0x0C, 0)));
        rec.Record(Snap(t0.AddSeconds(95), (0x0C, 900)));      // pulls away

        Assert.NotNull(rec.CurrentTripId);
        Assert.Single(new TripRepository(db).ForVehicle(Car));
    }

    [Fact]
    public void Recovers_a_trip_left_open_by_an_unclean_shutdown()
    {
        using var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Simulate iOS killing the app mid-drive: samples written, trip never closed.
        var rec = new TripRecorder(trips, samples, User, Car, new TripRecorderOptions { BatchSize = 1 });
        for (var i = 0; i < 10; i++)
            rec.Record(Snap(t0.AddSeconds(i * 10), (0x0C, 2000), (0x0D, 90)));

        var openTrip = trips.ForVehicle(Car).Single();
        Assert.Null(openTrip.EndedAt);

        // Next launch.
        var closed = trips.CloseOrphanedTrips(samples);

        Assert.Equal(1, closed);
        var recovered = trips.ForVehicle(Car).Single();
        Assert.Equal(t0.AddSeconds(90), recovered.EndedAt);   // last sample
        Assert.Equal(90, recovered.DurationS);
        Assert.NotNull(recovered.DistanceKm);                 // rebuilt, not left null
    }

    [Fact]
    public void Recovery_leaves_already_closed_trips_alone()
    {
        using var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);

        var rec = new TripRecorder(trips, samples, User, Car);
        rec.Record(Snap(DateTime.UtcNow, (0x0C, 800)));
        rec.OnDisconnected();
        var before = trips.ForVehicle(Car).Single();

        Assert.Equal(0, trips.CloseOrphanedTrips(samples));
        Assert.Equal(before.EndedAt, trips.ForVehicle(Car).Single().EndedAt);
    }

    [Fact]
    public void Live_and_recovered_distance_agree()
    {
        // The recorder integrates incrementally; recovery integrates the stored
        // trip in one pass. They must not drift apart.
        using var db = new MotorcitoDatabase(":memory:");
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = Car, UserId = User });
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var rec = new TripRecorder(trips, samples, User, Car, new TripRecorderOptions { BatchSize = 1 });
        var speeds = new double[] { 0, 20, 45, 70, 90, 90, 60, 30, 0 };
        for (var i = 0; i < speeds.Length; i++)
            rec.Record(Snap(t0.AddSeconds(i * 10), (0x0C, 1500), (0x0D, speeds[i])));

        rec.OnDisconnected();
        var live = trips.ForVehicle(Car).Single().DistanceKm;

        var recomputed = DistanceCalculator.Total(samples.GetForTrip(trips.ForVehicle(Car).Single().TripId));

        Assert.NotNull(live);
        Assert.Equal(recomputed, live!.Value, precision: 6);
    }

    [Fact]
    public void Disconnect_closes_the_open_trip()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;

        rec.Record(Snap(DateTime.UtcNow, (0x0C, 800)));
        rec.OnDisconnected();

        Assert.Null(rec.CurrentTripId);
        Assert.NotNull(new TripRepository(db).ForVehicle(Car).Single().EndedAt);
    }

    [Fact]
    public void Batches_inserts_rather_than_writing_every_sample()
    {
        var (db, rec) = NewRecorder(new TripRecorderOptions
        {
            BatchSize = 100,
            MaxBatchAge = TimeSpan.FromHours(1)   // isolate size-triggered flushing
        });
        using var _ = db;
        var samples = new SampleRepository(db);
        var t0 = DateTime.UtcNow;

        for (var i = 0; i < 50; i++)
            rec.Record(Snap(t0.AddSeconds(i), (0x0C, 800)));

        // Below the batch size, so nothing should have reached the database yet.
        Assert.Equal(0, samples.CountForTrip(rec.CurrentTripId!));

        for (var i = 50; i < 100; i++)
            rec.Record(Snap(t0.AddSeconds(i), (0x0C, 800)));

        Assert.Equal(100, samples.CountForTrip(rec.CurrentTripId!));
    }

    [Fact]
    public void Integrates_distance_from_speed()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;
        var t0 = DateTime.UtcNow;

        // A constant 100 kph for one hour, sampled each minute, is 100 km.
        for (var i = 0; i <= 60; i++)
            rec.Record(Snap(t0.AddMinutes(i), (0x0C, 2000), (0x0D, 100)));

        rec.OnDisconnected();

        var trip = new TripRepository(db).ForVehicle(Car).Single();
        Assert.NotNull(trip.DistanceKm);
        Assert.Equal(100.0, trip.DistanceKm!.Value, precision: 1);
    }

    [Fact]
    public void Persists_readings_into_the_right_columns()
    {
        var (db, rec) = NewRecorder();
        using var _ = db;
        var t0 = DateTime.UtcNow;

        rec.Record(Snap(t0,
            (0x0C, 2500),    // rpm
            (0x0D, 80),      // speed
            (0x05, 90),      // coolant
            (0x06, -3.5),    // stft1
            (0x07, 4.25)));  // ltft1
        rec.Flush();

        var stored = new SampleRepository(db)
            .GetWindow(Car, t0.AddMinutes(-1), t0.AddMinutes(1))
            .Single();

        Assert.Equal(2500, stored.Rpm);
        Assert.Equal(80, stored.SpeedKph);
        Assert.Equal(90, stored.CoolantC);
        Assert.Equal(-3.5, stored.Stft1Pct!.Value, precision: 3);
        Assert.Equal(4.25, stored.Ltft1Pct!.Value, precision: 3);
        // Not reported by this snapshot: must be NULL, never 0.
        Assert.Null(stored.MafGps);
    }

    [Fact]
    public void Rolling_buffer_keeps_only_its_window()
    {
        var buffer = new RollingSampleBuffer(TimeSpan.FromMinutes(5));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 600; i++)   // 10 minutes at 1 Hz
            buffer.Add(new Sample { TripId = "t", VehicleId = Car, Timestamp = t0.AddSeconds(i) });

        var kept = buffer.Snapshot();
        Assert.True(kept.Count <= 301, $"buffer kept {kept.Count}");
        Assert.True(kept[0].Timestamp >= t0.AddMinutes(4));
    }

    [Fact]
    public void Rolling_buffer_extracts_an_event_window()
    {
        var buffer = new RollingSampleBuffer(TimeSpan.FromMinutes(10));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 600; i++)
            buffer.Add(new Sample { TripId = "t", VehicleId = Car, Timestamp = t0.AddSeconds(i) });

        // ROADMAP: two minutes before the event, one after.
        var fault = t0.AddMinutes(5);
        var window = buffer.Around(fault);

        Assert.All(window, s => Assert.InRange(s.Timestamp, fault.AddMinutes(-2), fault.AddMinutes(1)));
        Assert.True(window.Count > 150);
    }
}
