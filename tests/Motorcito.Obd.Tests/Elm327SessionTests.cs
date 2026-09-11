using Motorcito.Obd;
using Motorcito.Obd.Testing;

namespace Motorcito.Obd.Tests;

public class Elm327SessionTests
{
    private static Elm327Options Fast => new()
    {
        CommandTimeout = TimeSpan.FromSeconds(1),
        InitTimeout = TimeSpan.FromSeconds(1),
        RetryDelay = TimeSpan.Zero
    };

    private static SimulatedObdAdapter Clean(params byte[] pids)
        => new(supportedPids: pids.Length > 0 ? pids : null,
               quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });

    [Fact]
    public async Task Runs_the_documented_init_sequence_in_order()
    {
        await using var adapter = Clean();
        await adapter.ConnectAsync();

        var result = await new Elm327Session(adapter, Fast).InitializeAsync();

        Assert.False(result.Failed);
        Assert.Equal(["ATZ", "ATE0", "ATL0", "ATS0", "ATSP0"], adapter.CommandLog);
    }

    [Fact]
    public async Task Reads_a_live_pid_end_to_end()
    {
        await using var adapter = Clean();
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);

        var rpm = await session.ReadAsync(PidRegistry.Find(0x0C)!);

        Assert.True(rpm.Success);
        Assert.InRange(rpm.Reading!.Value, 700, 2700);
    }

    [Fact]
    public async Task Scans_capabilities_and_reflects_the_configured_vehicle()
    {
        await using var adapter = Clean(0x04, 0x05, 0x06, 0x07, 0x0C, 0x0D, 0x0F, 0x11, 0x14);
        await adapter.ConnectAsync();

        var supported = await new Elm327Session(adapter, Fast).ScanSupportedPidsAsync();

        Assert.True(supported.IsSupported(0x06));
        Assert.True(supported.IsSupported(0x14));
        Assert.False(supported.IsSupported(0x10));   // no MAF on this car
        Assert.True(supported.EvaluatePhaseZeroGate().Passed);
    }

    [Fact]
    public async Task Stops_scanning_once_no_further_ranges_exist()
    {
        // All PIDs below 0x20, so only the first mask should be requested.
        await using var adapter = Clean(0x04, 0x05, 0x0C, 0x0D, 0x0F, 0x11);
        await adapter.ConnectAsync();

        await new Elm327Session(adapter, Fast).ScanSupportedPidsAsync();

        Assert.Equal(["0100"], adapter.CommandLog);
    }

    [Fact]
    public async Task Unsupported_pid_returns_no_data_and_is_not_retried()
    {
        await using var adapter = Clean(0x0C);
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);

        var result = await session.ReadAsync(PidRegistry.Find(0x10)!);   // MAF, absent

        Assert.False(result.Success);
        // Exactly one attempt: retrying a permanently-absent PID wastes budget.
        Assert.Single(adapter.CommandLog);
        Assert.Equal("0110", adapter.CommandLog[0]);
    }

    [Fact]
    public async Task Reads_the_vin()
    {
        await using var adapter = Clean();
        await adapter.ConnectAsync();

        var vin = await new Elm327Session(adapter, Fast).ReadVinAsync();

        Assert.Equal("WBS8M9C50J5K12345", vin);
    }

    [Fact]
    public async Task Reads_stored_trouble_codes()
    {
        await using var adapter = new SimulatedObdAdapter(
            storedDtcs: ["P0171", "P0300"],
            quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();

        var codes = await new Elm327Session(adapter, Fast).ReadDtcsAsync(DtcMode.Stored);

        Assert.Equal(["P0171", "P0300"], codes);
    }

    [Fact]
    public async Task Reports_no_codes_on_a_healthy_car()
    {
        await using var adapter = Clean();
        await adapter.ConnectAsync();

        Assert.Empty(await new Elm327Session(adapter, Fast).ReadDtcsAsync(DtcMode.Stored));
    }

    [Fact]
    public async Task Survives_clone_grade_firmware()
    {
        // The real acceptance test: echo, spaces, SEARCHING, bus errors and
        // truncated frames, over many reads. Nothing may throw, and enough
        // reads must succeed to be usable.
        await using var adapter = new SimulatedObdAdapter(
            quirks: new SimulatorQuirks
            {
                EchoCommands = true,
                EmitSpaces = true,
                SearchingRate = 0.15,
                BusErrorRate = 0.10,
                TruncationRate = 0.05,
                Latency = TimeSpan.Zero
            });
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);
        var rpm = PidRegistry.Find(0x0C)!;

        var succeeded = 0;
        for (var i = 0; i < 200; i++)
        {
            var result = await session.ReadAsync(rpm);
            if (result.Success)
            {
                succeeded++;
                // Any value that decodes must still be physically sane — a
                // truncated frame must never surface as a plausible reading.
                Assert.InRange(result.Reading!.Value, 0, 16383.75);
            }
        }

        Assert.True(succeeded > 150, $"Only {succeeded}/200 reads survived clone conditions.");
    }

    [Fact]
    public async Task Throws_when_used_before_connecting()
    {
        await using var adapter = Clean();

        await Assert.ThrowsAsync<ObdNotConnectedException>(
            () => adapter.SendCommandAsync("010C", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Reports_state_transitions()
    {
        await using var adapter = Clean();
        var states = new List<ObdConnectionState>();
        adapter.StateChanged += (_, s) => states.Add(s);

        await adapter.ConnectAsync();
        await adapter.DisconnectAsync();

        Assert.Equal(
            [ObdConnectionState.Connecting, ObdConnectionState.Connected, ObdConnectionState.Disconnected],
            states);
    }
}

/// <summary>
/// An adapter that makes the first bus request slow, as a real car does while
/// the ELM327 searches protocols after ATSP0.
/// </summary>
internal sealed class SlowNegotiatingAdapter : IObdAdapter
{
    private readonly TimeSpan _negotiationTime;
    private bool _negotiated;

    public SlowNegotiatingAdapter(TimeSpan negotiationTime) => _negotiationTime = negotiationTime;

    public string Name => "Slow negotiator";
    public ObdConnectionState State { get; private set; } = ObdConnectionState.Disconnected;
    public event EventHandler<ObdConnectionState>? StateChanged;

    /// <summary>Timeout the caller allowed for the first bus request.</summary>
    public TimeSpan FirstRequestBudget { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = ObdConnectionState.Connected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (command.StartsWith("AT", StringComparison.Ordinal))
            return "OK";

        if (!_negotiated)
        {
            FirstRequestBudget = timeout;

            // The caller's budget decides whether a real car would have been
            // given enough time to finish its protocol search.
            if (timeout < _negotiationTime)
                throw new ObdTimeoutException(command, timeout);

            _negotiated = true;
        }

        return command switch
        {
            "0100" => "41 00 BE 3E B8 11",
            "010C" => "41 0C 1A F8",
            _ => "NO DATA"
        };
    }

    public Task DisconnectAsync()
    {
        State = ObdConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ProtocolNegotiationTests
{
    [Fact]
    public async Task The_first_bus_request_is_given_time_to_negotiate()
    {
        // Reproduces a real failure: on a healthy car the capability scan
        // reported no PIDs at all, because ATSP0 only selects auto-detection
        // and the actual protocol search happens on the first 0100 — which was
        // being judged by the 1.5 s steady-state timeout.
        await using var adapter = new SlowNegotiatingAdapter(TimeSpan.FromSeconds(8));
        await adapter.ConnectAsync();

        var session = new Elm327Session(adapter, new Elm327Options { RetryDelay = TimeSpan.Zero });
        await session.InitializeAsync();

        var supported = await session.ScanSupportedPidsAsync();

        Assert.True(adapter.FirstRequestBudget >= TimeSpan.FromSeconds(8),
            $"first request was only allowed {adapter.FirstRequestBudget.TotalSeconds:0.#} s");
        Assert.NotEmpty(supported.Pids);
        Assert.False(supported.EvaluatePhaseZeroGate().ScanLooksImplausible);
    }

    [Fact]
    public async Task Steady_state_reads_keep_the_short_timeout()
    {
        // The long budget must apply only until the bus answers. Otherwise a
        // PID the ECU ignores would cost fifteen seconds on every cycle.
        await using var adapter = new SlowNegotiatingAdapter(TimeSpan.FromSeconds(1));
        await adapter.ConnectAsync();

        var options = new Elm327Options { RetryDelay = TimeSpan.Zero };
        var session = new Elm327Session(adapter, options);
        await session.InitializeAsync();
        await session.ScanSupportedPidsAsync();

        var before = adapter.FirstRequestBudget;
        await session.ReadAsync(PidRegistry.Find(0x0C)!);

        Assert.Equal(options.FirstRequestTimeout, before);
    }
}
