using Motorcito.Data;
using Motorcito.Obd;
using Motorcito.Obd.Testing;

namespace Motorcito.Data.Tests;

/// <summary>
/// The whole pipeline, with no hardware: simulated vehicle → ELM327 session →
/// poller → recorder → SQLite.
///
/// This is what proves the logging path before the MX+ arrives. When it does,
/// the only thing that changes is which <see cref="IObdAdapter"/> is injected.
/// </summary>
public class EndToEndLoggingTests
{
    private const string User = "user-1";

    [Fact]
    public async Task Simulated_drive_produces_a_trip_with_samples()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var vehicles = new VehicleRepository(db);
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);

        await using var adapter = new SimulatedObdAdapter(
            quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();

        var session = new Elm327Session(adapter, new Elm327Options
        {
            CommandTimeout = TimeSpan.FromSeconds(1),
            InitTimeout = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.Zero
        });

        await session.InitializeAsync();
        var supported = await session.ScanSupportedPidsAsync();
        var vin = await session.ReadVinAsync();

        // Capability scan and VIN are captured at connect, exactly as the app does.
        var vehicle = vehicles.Upsert(new Vehicle
        {
            VehicleId = Guid.NewGuid().ToString("N"),
            UserId = User,
            Vin = vin,
            SupportedPidsJson = supported.ToJson()
        });

        Assert.Equal("WBS8M9C50J5K12345", vehicle.Vin);
        Assert.True(supported.EvaluatePhaseZeroGate().Passed);

        var recorder = new TripRecorder(trips, samples, User, vehicle.VehicleId,
            new TripRecorderOptions { BatchSize = 50 });

        var poller = new ObdPoller(session, supported);
        poller.SnapshotUpdated += (_, snapshot) => recorder.Record(snapshot);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await poller.RunAsync(cts.Token);

        recorder.OnDisconnected();

        var trip = Assert.Single(trips.ForVehicle(vehicle.VehicleId));
        Assert.NotNull(trip.EndedAt);

        var count = samples.CountForTrip(trip.TripId);
        Assert.True(count > 20, $"only {count} samples logged");

        // Values must be physically plausible, not merely present.
        var stored = samples.GetWindow(vehicle.VehicleId, trip.StartedAt.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.All(stored, s =>
        {
            if (s.Rpm is { } rpm) Assert.InRange(rpm, 0, 16383.75);
            if (s.CoolantC is { } c) Assert.InRange(c, -40, 215);
            if (s.ThrottlePct is { } t) Assert.InRange(t, 0, 100);
        });
    }

    [Fact]
    public async Task Logging_survives_clone_grade_adapter_junk()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var trips = new TripRepository(db);
        var samples = new SampleRepository(db);
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = "car-1", UserId = User });

        await using var adapter = new SimulatedObdAdapter(quirks: new SimulatorQuirks
        {
            EchoCommands = true,
            EmitSpaces = true,
            SearchingRate = 0.15,
            BusErrorRate = 0.10,
            TruncationRate = 0.05,
            Latency = TimeSpan.Zero
        });
        await adapter.ConnectAsync();

        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });
        await session.InitializeAsync();
        var supported = await session.ScanSupportedPidsAsync();

        var recorder = new TripRecorder(trips, samples, User, "car-1",
            new TripRecorderOptions { BatchSize = 25 });

        var poller = new ObdPoller(session, supported);
        poller.SnapshotUpdated += (_, s) => recorder.Record(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await poller.RunAsync(cts.Token);
        recorder.OnDisconnected();

        // A bad adapter should degrade the sample rate, never corrupt the log.
        var trip = Assert.Single(trips.ForVehicle("car-1"));
        Assert.True(samples.CountForTrip(trip.TripId) > 0);
    }

    [Fact]
    public async Task Capability_scan_drives_which_columns_get_populated()
    {
        using var db = new MotorcitoDatabase(":memory:");
        var samples = new SampleRepository(db);
        new VehicleRepository(db).Upsert(new Vehicle { VehicleId = "car-1", UserId = User });

        // An inline engine: no bank 2 trims, no MAF (speed-density).
        await using var adapter = new SimulatedObdAdapter(
            supportedPids: [0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, 0x14],
            quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();

        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });
        await session.InitializeAsync();
        var supported = await session.ScanSupportedPidsAsync();

        var recorder = new TripRecorder(new TripRepository(db), samples, User, "car-1",
            new TripRecorderOptions { BatchSize = 10 });
        var poller = new ObdPoller(session, supported);
        poller.SnapshotUpdated += (_, s) => recorder.Record(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await poller.RunAsync(cts.Token);
        recorder.Flush();

        var stored = samples.GetWindow("car-1", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        Assert.NotEmpty(stored);

        // Unsupported PIDs must stay NULL — never defaulted to zero, which would
        // silently corrupt any baseline computed over them.
        Assert.All(stored, s =>
        {
            Assert.Null(s.Stft2Pct);
            Assert.Null(s.Ltft2Pct);
            Assert.Null(s.MafGps);
        });
        Assert.Contains(stored, s => s.Ltft1Pct is not null);
    }
}
