using Motorcito.Obd;
using Motorcito.Obd.Signals;
using Motorcito.Obd.Testing;

namespace Motorcito.Obd.Tests;

/// <summary>
/// The connect-time probe and extended polling, against the simulated MX-5.
/// Commands are built here rather than loaded from a catalog, so these tests
/// never depend on a removable data source.
/// </summary>
public class SignalProbeTests
{
    private static Elm327Options Fast => new()
    {
        CommandTimeout = TimeSpan.FromSeconds(1),
        InitTimeout = TimeSpan.FromSeconds(1),
        RetryDelay = TimeSpan.Zero
    };

    private static async Task<(SimulatedObdAdapter Adapter, Elm327Session Session)> ConnectMx5()
    {
        var adapter = SimulatedObdAdapter.MazdaMx5Nd(new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);
        await session.InitializeAsync();
        return (adapter, session);
    }

    private static SignalCommand Command(
        string header,
        ushort identifier,
        string? canonicalKey,
        double divisor = 1,
        double offset = 0,
        string unit = SignalUnits.None,
        int bitLength = 16,
        double multiplier = 1,
        SignalQualifier? qualifier = null) => new()
    {
        SourceId = "test",
        Header = header,
        Service = 0x22,
        Identifier = identifier,
        IdentifierLength = 2,
        Signals =
        [
            new SignalDefinition
            {
                Id = $"S{identifier:X4}",
                Name = "signal",
                BitLength = bitLength,
                Multiplier = multiplier,
                Divisor = divisor,
                Offset = offset,
                Unit = unit,
                CanonicalKey = canonicalKey,
                Qualifier = qualifier,
            }
        ],
    };

    private static SignalCommand OilTemp(string header = "7E0")
        => Command(header, 0x1310, CanonicalSignals.OilTemp.Key, divisor: 100, offset: -40, unit: SignalUnits.Celsius);

    [Fact]
    public async Task Each_kind_of_answer_is_classified()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        var results = await SignalProbe.ProbeAsync(session,
        [
            OilTemp(),
            Command("7E0", 0xBEEF, CanonicalSignals.OilTemp.Key),
            OilTemp(header: "7E5"),
            Command("7E0", 0x16E8, canonicalKey: null),
            // Oil temperature decoded without its scaling: thousands of degrees.
            Command("7E0", 0x1310, CanonicalSignals.OilTemp.Key, unit: SignalUnits.Celsius),
            OilTemp() with { Header = "18DA10F1" },
        ]);

        Assert.Equal(6, results.Count);

        // Answered with a plausible oil temperature.
        Assert.Contains(results, r => r.Command.Identifier == 0x1310 && r.Command.Header == "7E0"
            && r.Command.Signals[0].Divisor == 100 && r.State == ProbeState.Verified);

        // The engine module exists but refuses an identifier it does not know (7F 31).
        Assert.Contains(results, r => r.Command.Identifier == 0xBEEF && r.State == ProbeState.Unsupported);

        // Nothing listens at 7E5.
        Assert.Contains(results, r => r.Command.Header == "7E5" && r.State == ProbeState.Unsupported);

        // Answered, but nothing in it maps to a value the app uses.
        Assert.Contains(results, r => r.Command.Identifier == 0x16E8 && r.State == ProbeState.AnsweredUnmapped);

        // The same oil temperature bytes decoded without scaling are thousands of degrees.
        Assert.Contains(results, r => r.Command.Identifier == 0x1310 && r.Command.Header == "7E0"
            && r.Command.Signals[0].Divisor == 1 && r.State == ProbeState.Implausible);

        // A 29-bit header is reported, never sent with approximate addressing.
        Assert.Contains(results, r => r.Command.Header == "18DA10F1" && r.State == ProbeState.UnsupportedByApp);
    }

    [Fact]
    public async Task Probing_leaves_the_adapter_on_the_functional_header()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        await SignalProbe.ProbeAsync(session, [OilTemp(), Command("720", 0x2A05, canonicalKey: null)]);

        Assert.Equal(SignalRequest.FunctionalHeader, session.CurrentHeader);
    }

    [Fact]
    public async Task Driver_behaviour_data_is_never_verified_for_collection()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        var result = Assert.Single(await SignalProbe.ProbeAsync(session,
            [Command("7E0", 0x0967, CanonicalSignals.SeatbeltBuckled.Key, bitLength: 8, unit: SignalUnits.OnOff)]));

        Assert.Equal(ProbeState.AnsweredUnmapped, result.State);
        Assert.Equal(SignalCheck.BlockedForPrivacy, Assert.Single(result.Signals).Check);
    }

    [Fact]
    public async Task Verified_reads_are_polled_alongside_standard_obd()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;
        var supported = await session.ScanSupportedPidsAsync();

        var poller = new ObdPoller(session, supported, extendedCommands: [OilTemp()]);

        ObdSnapshot? seen = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        poller.SnapshotUpdated += (_, snapshot) =>
        {
            if (snapshot.ExtendedValue(CanonicalSignals.OilTemp.Key) is not null)
            {
                seen = snapshot;
                cts.Cancel();
            }
        };

        try
        {
            await poller.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(seen);
        Assert.InRange(seen.ExtendedValue(CanonicalSignals.OilTemp.Key)!.Value, 15, 110);
        Assert.NotNull(seen.Value(0x0C));
        Assert.Contains("ATSH7DF", adapter.CommandLog);
    }

    [Fact]
    public async Task Slow_changing_values_are_not_re_read_every_cycle()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;
        var supported = await session.ScanSupportedPidsAsync();

        var tirePressure = Command("720", 0x2A05, CanonicalSignals.TirePressure.Key,
            bitLength: 8, multiplier: 1373, divisor: 100000, unit: SignalUnits.Bar,
            qualifier: SignalQualifier.WheelNumber(1));

        var poller = new ObdPoller(session, supported, extendedCommands: [tirePressure]);

        var cycles = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        poller.SnapshotUpdated += (_, _) =>
        {
            if (++cycles >= 10)
                cts.Cancel();
        };

        try
        {
            await poller.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Tire pressure is paced at 30 s: ten fast cycles read it once.
        Assert.True(cycles >= 10);
        Assert.Single(adapter.CommandLog, c => c == "222A05");
    }

    [Fact]
    public async Task A_damaged_reply_is_retried_before_a_signal_is_written_off()
    {
        await using var inner = SimulatedObdAdapter.MazdaMx5Nd(new SimulatorQuirks { Latency = TimeSpan.Zero });
        await using var adapter = new DropsLastByteOnce(inner, "221310");
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);
        await session.InitializeAsync();

        var result = Assert.Single(await SignalProbe.ProbeAsync(session, [OilTemp()]));

        Assert.Equal(ProbeState.Verified, result.State);
        Assert.Equal(2, inner.CommandLog.Count(c => c == "221310"));
    }

    /// <summary>
    /// Cuts the last byte off the first reply to one command, as a cheap
    /// adapter dropping a frame does — the exact failure seen in the simulator.
    /// </summary>
    private sealed class DropsLastByteOnce(IObdAdapter inner, string command) : IObdAdapter
    {
        private bool _dropped;

        public string Name => inner.Name;

        public ObdConnectionState State => inner.State;

        public event EventHandler<ObdConnectionState>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

        public Task DisconnectAsync() => inner.DisconnectAsync();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task<string> SendCommandAsync(string sent, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var reply = await inner.SendCommandAsync(sent, timeout, cancellationToken);
            if (_dropped || sent != command)
                return reply;

            _dropped = true;
            return reply.TrimEnd('\r')[..^2] + "\r";
        }
    }
}
