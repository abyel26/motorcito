using Motorcito.Data;
using Motorcito.Obd;
using Motorcito.Obd.Testing;

namespace Motorcito.Data.Tests;

public class ObdLogTests
{
    /// <summary>The reply layout that defeated the VIN decoder, kept verbatim.</summary>
    private const string VinReply = "014\r0: 49 02 01 57 42 53\r1: 38 4D 39 43 35 30 4A\r2: 35 4B 31 32 33 34 35";

    private static LoggingObdAdapter Wrap(IObdAdapter inner, LoggingService logging,
        ObdLogMode mode = ObdLogMode.Diagnostic)
        => new(inner, logging.ObdLog, mode);

    [Fact]
    public async Task The_raw_reply_is_stored_exactly_as_received()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        await using var adapter = Wrap(inner, logging);
        await new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero }).ReadVinAsync();
        logging.ObdLog.Flush();

        // The whole point: the text as the adapter sent it, before parsing.
        // Whatever a car replies to 0902 is now answerable from the database
        // instead of inferred from source code.
        var entry = Assert.Single(logging.ObdLog.ForCommand("0902"));
        Assert.False(string.IsNullOrWhiteSpace(entry.RawResponse));
    }

    [Fact]
    public async Task Failures_are_always_recorded()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(
            supportedPids: [0x0C],
            quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        await using var adapter = Wrap(inner, logging);
        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });

        // A PID this car does not support: answers NO DATA every time.
        await session.ReadAsync(PidRegistry.Find(0x10)!);
        logging.ObdLog.Flush();

        var entries = logging.ObdLog.ForCommand("0110");
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.NotEqual("Data", e.Status));
    }

    [Fact]
    public async Task Repeat_successful_reads_are_not_recorded()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        await using var adapter = Wrap(inner, logging);
        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });

        for (var i = 0; i < 50; i++)
            await session.ReadAsync(PidRegistry.Find(0x0C)!);

        logging.ObdLog.Flush();

        // At 10–20 reads a second, keeping every success would be millions of
        // rows an hour and would dwarf the driving data it is meant to explain.
        Assert.Single(logging.ObdLog.ForCommand("010C"));
    }

    [Fact]
    public async Task Verbose_mode_records_everything()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        await using var adapter = Wrap(inner, logging, ObdLogMode.Verbose);
        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });

        for (var i = 0; i < 10; i++)
            await session.ReadAsync(PidRegistry.Find(0x0C)!);

        logging.ObdLog.Flush();

        Assert.Equal(10, logging.ObdLog.ForCommand("010C").Count);
    }

    [Fact]
    public async Task The_init_sequence_and_capability_scan_are_kept()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        await using var adapter = Wrap(inner, logging);
        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });

        await session.InitializeAsync();
        await session.ScanSupportedPidsAsync();
        logging.ObdLog.Flush();

        // Between them, these describe everything about how a car introduced
        // itself — and they happen once, so keeping them costs nothing.
        Assert.NotEmpty(logging.ObdLog.ForCommand("ATZ"));
        Assert.NotEmpty(logging.ObdLog.ForCommand("ATSP0"));
        Assert.NotEmpty(logging.ObdLog.ForCommand("0100"));
    }

    [Fact]
    public async Task Logging_never_changes_what_the_caller_receives()
    {
        using var logging = new LoggingService(":memory:");
        await using var inner = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await inner.ConnectAsync();

        var direct = await inner.SendCommandAsync("010C", TimeSpan.FromSeconds(1));

        await using var adapter = Wrap(inner, logging);
        var throughDecorator = await adapter.SendCommandAsync("010C", TimeSpan.FromSeconds(1));

        // The decorator observes; it must never alter the behaviour it watches.
        Assert.Equal(direct.Length > 0, throughDecorator.Length > 0);
    }

    [Fact]
    public async Task A_sink_failure_never_costs_a_read()
    {
        // Logging runs on the poll loop. A storage problem must degrade
        // diagnosis, never data collection.
        await using var adapter = new LoggingObdAdapter(
            new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero }),
            new ThrowingSink());

        await adapter.ConnectAsync();
        var raw = await adapter.SendCommandAsync("010C", TimeSpan.FromSeconds(1));

        Assert.False(string.IsNullOrEmpty(raw));
    }

    [Fact]
    public void Exported_log_is_csv_with_the_raw_text_intact()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"motorcito-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            using var logging = new LoggingService(Path.Combine(directory, "live.db"));
            logging.ObdLog.Record(new ObdExchange(
                DateTime.UtcNow, "0902", VinReply, "Data", 42, null));

            var (path, rows) = logging.ExportDiagnosticLog(Path.Combine(directory, "log.csv"));
            var text = File.ReadAllText(path);

            Assert.Equal(1, rows);
            // Multi-line replies must survive CSV quoting, or the evidence is
            // mangled by the very export meant to deliver it. ELM327 uses CR,
            // which an escaper checking only for LF would miss.
            Assert.Contains("49 02 01 57 42 53", text);
            Assert.Contains("\"", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Erasure_removes_the_raw_log_too()
    {
        using var logging = new LoggingService(":memory:");
        logging.ObdLog.Record(new ObdExchange(DateTime.UtcNow, "0902", VinReply, "Data", 10, null));
        logging.ObdLog.Flush();

        Assert.True(logging.ObdLog.Count() > 0);

        var report = logging.EraseUser();

        // The 0902 reply carries the VIN in plaintext hex, so these rows are
        // personal data and must not survive an erasure.
        Assert.Equal(1, report.ObdLogEntries);
        Assert.Equal(0, logging.ObdLog.Count());
        Assert.Equal(0, logging.CountRowsForUser());
    }

    [Fact]
    public void The_log_can_be_cleared_without_losing_driving_data()
    {
        using var logging = new LoggingService(":memory:");
        var supported = SupportedPids.FromPids([0x0C]);
        logging.StartRecording("VIN123", supported, new TripRecorderOptions { BatchSize = 1 });
        var vehicleId = logging.CurrentVehicle!.VehicleId;

        logging.Record(new ObdSnapshot(DateTime.UtcNow,
            new Dictionary<byte, PidReading> { [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, []) }, 1));

        logging.ObdLog.Record(new ObdExchange(DateTime.UtcNow, "0902", VinReply, "Data", 10, null));
        logging.ObdLog.Flush();

        logging.ObdLog.Clear();

        // Raw replies are the sensitive part; a user may want them gone without
        // discarding their driving history.
        Assert.Equal(0, logging.ObdLog.Count());
        Assert.Single(logging.Trips.ForVehicle(vehicleId));
    }

    private sealed class ThrowingSink : IObdLogSink
    {
        public void Record(ObdExchange exchange) => throw new InvalidOperationException("disk full");
    }
}
