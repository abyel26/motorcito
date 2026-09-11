using Motorcito.Data;
using Motorcito.Obd;
using Motorcito.Obd.Testing;

namespace Motorcito.Data.Tests;

public class VehicleIdentityTests
{
    private const string User = LoggingService.LocalUserId;

    [Fact]
    public void The_same_vin_always_resolves_to_the_same_vehicle()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicles = new VehicleRepository(db);

        // Reconnecting to the same car must not create a second row: baselines
        // accumulate per vehicle, and a new row would silently reset history.
        var first = vehicles.ResolveOrCreate(User, "WBS8M9C50J5K12345", "[1,2]");
        var second = vehicles.ResolveOrCreate(User, "WBS8M9C50J5K12345", "[1,2,3]");

        Assert.Equal(first.VehicleId, second.VehicleId);
        Assert.Single(vehicles.All(User));
    }

    [Fact]
    public void Different_vins_resolve_to_different_vehicles()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicles = new VehicleRepository(db);

        var m3 = vehicles.ResolveOrCreate(User, "WBS8M9C50J5K12345", null);
        var mx5 = vehicles.ResolveOrCreate(User, "JM1NDAD75M0123456", null);

        Assert.NotEqual(m3.VehicleId, mx5.VehicleId);
        Assert.Equal(2, vehicles.All(User).Count);
    }

    [Fact]
    public void A_car_with_no_vin_still_resolves_to_one_stable_vehicle()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicles = new VehicleRepository(db);

        // Without this, every connection to a VIN-less car would create a new
        // vehicle and no bucket would ever reach the sufficiency gate.
        var first = vehicles.ResolveOrCreate(User, null, null);
        var second = vehicles.ResolveOrCreate(User, null, null);

        Assert.Equal(first.VehicleId, second.VehicleId);
        Assert.Single(vehicles.All(User));
    }

    [Fact]
    public void Capability_scan_is_refreshed_on_every_connection()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicles = new VehicleRepository(db);

        // A module asleep during one scan reports more PIDs the next time, so
        // the stored scan overwrites rather than coalescing.
        vehicles.ResolveOrCreate(User, "VIN123", "[4,5]");
        var updated = vehicles.ResolveOrCreate(User, "VIN123", "[4,5,6,7]");

        Assert.Equal("[4,5,6,7]", vehicles.Get(updated.VehicleId)!.SupportedPidsJson);
    }
}

public class LoggingServiceTests
{
    [Fact]
    public async Task Records_a_simulated_drive_through_the_service()
    {
        using var logging = new LoggingService(":memory:");

        await using var adapter = new SimulatedObdAdapter(
            quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();

        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });
        await session.InitializeAsync();
        var supported = await session.ScanSupportedPidsAsync();
        var vin = await session.ReadVinAsync();

        var recorder = logging.StartRecording(vin, supported, new TripRecorderOptions { BatchSize = 25 });
        Assert.NotNull(logging.CurrentVehicle);
        Assert.Equal(vin, logging.CurrentVehicle!.Vin);

        var poller = new ObdPoller(session, supported);
        poller.SnapshotUpdated += (_, s) => logging.Record(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await poller.RunAsync(cts.Token);

        logging.StopRecording();

        var trip = Assert.Single(logging.Trips.ForVehicle(
            logging.Vehicles.All(LoggingService.LocalUserId).Single().VehicleId));
        Assert.NotNull(trip.EndedAt);
        Assert.True(logging.Samples.CountForTrip(trip.TripId) > 10);
        Assert.Null(logging.Recorder);
    }

    [Fact]
    public void Recording_is_a_no_op_before_it_is_started()
    {
        using var logging = new LoggingService(":memory:");

        // The lifecycle flush and snapshot handler can both fire before a
        // connection exists; neither may throw.
        logging.Record(new ObdSnapshot(DateTime.UtcNow, new Dictionary<byte, PidReading>(), 0));
        Assert.Equal(0, logging.Flush());
        logging.StopRecording();
    }

    [Fact]
    public void Flush_writes_without_ending_the_trip()
    {
        using var logging = new LoggingService(":memory:");
        var supported = SupportedPids.FromPids([0x0C, 0x0D]);

        var recorder = logging.StartRecording("VIN123", supported,
            new TripRecorderOptions { BatchSize = 1000, MaxBatchAge = TimeSpan.FromHours(1) });

        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            logging.Record(new ObdSnapshot(t0.AddSeconds(i), new Dictionary<byte, PidReading>
            {
                [0x0C] = new(PidRegistry.Find(0x0C)!, 1500, [])
            }, 1));
        }

        var tripId = recorder.CurrentTripId!;
        Assert.Equal(0, logging.Samples.CountForTrip(tripId));   // still buffered

        Assert.Equal(5, logging.Flush());
        Assert.Equal(5, logging.Samples.CountForTrip(tripId));

        // Backgrounding is not the drive ending — the trip must stay open.
        Assert.NotNull(recorder.CurrentTripId);
        Assert.Null(logging.Trips.Get(tripId)!.EndedAt);
    }

    [Fact]
    public void Startup_recovery_repairs_a_trip_from_a_previous_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"motorcito-test-{Guid.NewGuid():N}.db");
        try
        {
            var supported = SupportedPids.FromPids([0x0C]);
            var t0 = DateTime.UtcNow;

            // First run: terminated mid-drive.
            //
            // Deliberately not via LoggingService — disposing that closes the
            // trip cleanly, which is right for an orderly shutdown but is the
            // opposite of what needs simulating here. Driving the repositories
            // directly and abandoning the trip is what iOS killing the process
            // actually looks like.
            using (var db = new MotorcitoDatabase(path))
            {
                var trips = new TripRepository(db);
                var samples = new SampleRepository(db);
                var vehicle = new VehicleRepository(db)
                    .ResolveOrCreate(LoggingService.LocalUserId, "VIN123", supported.ToJson());

                var recorder = new TripRecorder(trips, samples, LoggingService.LocalUserId,
                    vehicle.VehicleId, new TripRecorderOptions { BatchSize = 1 });

                for (var i = 0; i < 5; i++)
                {
                    recorder.Record(new ObdSnapshot(t0.AddSeconds(i * 10), new Dictionary<byte, PidReading>
                    {
                        [0x0C] = new(PidRegistry.Find(0x0C)!, 1500, [])
                    }, 1));
                }

                Assert.Null(trips.ForVehicle(vehicle.VehicleId).Single().EndedAt);
            }

            // Second run: the app starts again and repairs it.
            using var second = new LoggingService(path);
            Assert.Equal(1, second.RecoverOrphanedTrips());

            var vehicleId = second.Vehicles.All(LoggingService.LocalUserId).Single().VehicleId;
            Assert.NotNull(second.Trips.ForVehicle(vehicleId).Single().EndedAt);

            // Idempotent: a clean second startup repairs nothing.
            Assert.Equal(0, second.RecoverOrphanedTrips());
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}*"))
                File.Delete(file);
        }
    }
}
