using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

/// <summary>
/// An adapter whose every response is dictated by the test.
///
/// <see cref="Motorcito.Obd.Testing.SimulatedObdAdapter"/> models a plausible
/// car with randomised quirks, which is right for end-to-end work and wrong
/// here: the poller's interesting behaviour is what it does when a specific PID
/// fails a specific number of times, and that needs determinism.
/// </summary>
internal sealed class ScriptedAdapter : IObdAdapter
{
    private readonly Dictionary<byte, string> _responses = [];
    private readonly HashSet<byte> _failing = [];
    private readonly HashSet<byte> _throwing = [];

    public string Name => "Scripted";
    public ObdConnectionState State { get; private set; } = ObdConnectionState.Disconnected;
    public event EventHandler<ObdConnectionState>? StateChanged;

    public List<string> CommandLog { get; } = [];

    /// <summary>Commands seen for one PID, e.g. "010C".</summary>
    public int CountFor(byte pid) => CommandLog.Count(c => c == $"01{pid:X2}");

    public ScriptedAdapter Answers(byte pid, string response)
    {
        _responses[pid] = response;
        return this;
    }

    /// <summary>This PID replies NO DATA — a decode failure, not a transport failure.</summary>
    public ScriptedAdapter RepliesNoData(byte pid)
    {
        _failing.Add(pid);
        return this;
    }

    /// <summary>This PID throws at the transport layer, as a dropped link would.</summary>
    public ScriptedAdapter Throws(byte pid)
    {
        _throwing.Add(pid);
        return this;
    }

    public ScriptedAdapter Recovers(byte pid)
    {
        _failing.Remove(pid);
        _throwing.Remove(pid);
        return this;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        State = ObdConnectionState.Connected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        CommandLog.Add(command);

        // Yield before answering.
        //
        // A real transport always awaits I/O, so the poll loop returns to its
        // caller on the first read. Answering synchronously instead makes
        // RunAsync run to completion on the calling thread and never hand back
        // a Task — which hung a cancellation test for two hours, because the
        // line that was meant to cancel it was never reached.
        await Task.Yield();

        if (command.StartsWith("01", StringComparison.Ordinal) && command.Length >= 4
            && byte.TryParse(command.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var pid))
        {
            if (_throwing.Contains(pid))
                throw new ObdTimeoutException(command, timeout);

            if (_failing.Contains(pid))
                return "NO DATA";

            if (_responses.TryGetValue(pid, out var scripted))
                return scripted;
        }

        return "OK";
    }

    public Task DisconnectAsync()
    {
        State = ObdConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class ObdPollerTests
{
    private static Elm327Options Fast => new()
    {
        CommandTimeout = TimeSpan.FromSeconds(1),
        InitTimeout = TimeSpan.FromSeconds(1),
        FirstRequestTimeout = TimeSpan.FromSeconds(1),
        RetryDelay = TimeSpan.Zero
    };

    /// <summary>Runs the poller until <paramref name="cycles"/> snapshots have been emitted.</summary>
    private static async Task<List<ObdSnapshot>> RunCycles(ObdPoller poller, int cycles)
    {
        var seen = new List<ObdSnapshot>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));   // safety net

        void OnSnapshot(object? sender, ObdSnapshot snapshot)
        {
            seen.Add(snapshot);
            if (seen.Count >= cycles)
                cts.Cancel();
        }

        poller.SnapshotUpdated += OnSnapshot;
        try
        {
            await poller.RunAsync(cts.Token);
        }
        finally
        {
            // Must unsubscribe: a poller reused for a second run would
            // otherwise still call this handler, cancelling a disposed token
            // source and throwing out of the event.
            poller.SnapshotUpdated -= OnSnapshot;
        }

        return seen;
    }

    private static (ScriptedAdapter adapter, Elm327Session session) Connected()
    {
        var adapter = new ScriptedAdapter()
            .Answers(0x0C, "41 0C 1A F8")     // rpm    — Fast
            .Answers(0x0D, "41 0D 3C")        // speed  — Fast
            .Answers(0x06, "41 06 80")        // stft1  — Medium
            .Answers(0x05, "41 05 5A");       // coolant— Slow

        adapter.ConnectAsync().GetAwaiter().GetResult();
        return (adapter, new Elm327Session(adapter, Fast));
    }

    [Fact]
    public void Rotation_puts_fast_parameters_first()
    {
        var (_, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x05, 0x06, 0x0C]));

        // Fast parameters are read first so they are freshest when the snapshot
        // is emitted at the end of the cycle.
        Assert.Equal(PollClass.Fast, poller.Rotation[0].Poll);
        Assert.Equal(PollClass.Slow, poller.Rotation[^1].Poll);
    }

    [Fact]
    public async Task Fast_parameters_are_read_every_cycle()
    {
        var (adapter, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C]),
            new PollerOptions { CycleDelay = TimeSpan.Zero });

        await RunCycles(poller, 5);

        Assert.Equal(5, adapter.CountFor(0x0C));
    }

    [Fact]
    public async Task Medium_and_slow_parameters_are_read_less_often()
    {
        var (adapter, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x06, 0x05]),
            new PollerOptions { MediumEveryNCycles = 4, SlowEveryNCycles = 20 });

        await RunCycles(poller, 20);

        // Every read is a round trip; polling coolant as often as RPM would cost
        // most of the RPM resolution on a slow adapter.
        Assert.Equal(20, adapter.CountFor(0x0C));                 // fast: every cycle
        Assert.InRange(adapter.CountFor(0x06), 4, 6);             // medium: cycles 0,4,8,12,16
        Assert.InRange(adapter.CountFor(0x05), 1, 2);             // slow: cycle 0 (and 20 if reached)
    }

    [Fact]
    public async Task A_pid_that_never_answers_is_dropped()
    {
        var (adapter, session) = Connected();
        adapter.RepliesNoData(0x0D);

        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { FailuresBeforeDropping = 3 });

        await RunCycles(poller, 8);

        // The capability scan claimed it, the ECU will not answer it. Without
        // dropping, it burns a round trip every cycle forever.
        Assert.Contains((byte)0x0D, poller.DroppedPids);
        Assert.Equal(3, adapter.CountFor(0x0D));
        Assert.Equal(8, adapter.CountFor(0x0C));   // the healthy PID is unaffected
    }

    [Fact]
    public async Task A_dropped_pid_is_never_queried_again()
    {
        var (adapter, session) = Connected();
        adapter.RepliesNoData(0x0D);

        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { FailuresBeforeDropping = 2 });

        await RunCycles(poller, 3);
        var afterDropping = adapter.CountFor(0x0D);

        await RunCycles(poller, 5);

        Assert.Equal(afterDropping, adapter.CountFor(0x0D));
    }

    [Fact]
    public async Task An_intermittent_pid_is_not_dropped()
    {
        // The counter must be consecutive, not cumulative. A PID that fails
        // occasionally over a long drive is normal; dropping it would silently
        // lose a parameter for the rest of the trip.
        var (adapter, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { FailuresBeforeDropping = 3 });

        var cycle = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        poller.SnapshotUpdated += (_, _) =>
        {
            cycle++;
            // Fail, recover, fail, recover — never three in a row.
            if (cycle % 2 == 0) adapter.RepliesNoData(0x0D);
            else adapter.Recovers(0x0D);

            if (cycle >= 12)
                cts.Cancel();
        };

        await poller.RunAsync(cts.Token);

        Assert.DoesNotContain((byte)0x0D, poller.DroppedPids);
    }

    [Fact]
    public async Task A_transport_failure_does_not_kill_the_loop()
    {
        var (adapter, session) = Connected();
        adapter.Throws(0x0D);

        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { FailuresBeforeDropping = 3 });

        var snapshots = await RunCycles(poller, 6);

        // A thrown timeout must be absorbed: the car being switched off mid-trip
        // is routine, and the loop has to survive it.
        Assert.Equal(6, snapshots.Count);
        Assert.Contains((byte)0x0D, poller.DroppedPids);
    }

    [Fact]
    public async Task Snapshots_carry_the_latest_decoded_values()
    {
        var (_, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]));

        var snapshots = await RunCycles(poller, 3);
        var last = snapshots[^1];

        // 41 0C 1A F8 -> (0x1AF8)/4 = 1726 rpm; 41 0D 3C -> 60 km/h
        Assert.Equal(1726, last.Value(0x0C));
        Assert.Equal(60, last.Value(0x0D));
    }

    [Fact]
    public async Task A_failing_pid_keeps_its_last_good_value()
    {
        var (adapter, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { FailuresBeforeDropping = 99 });

        var snapshots = new List<ObdSnapshot>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        poller.SnapshotUpdated += (_, s) =>
        {
            snapshots.Add(s);
            if (snapshots.Count == 2)
                adapter.RepliesNoData(0x0D);      // speed stops answering
            if (snapshots.Count >= 6)
                cts.Cancel();
        };

        await poller.RunAsync(cts.Token);

        // Stale rather than absent: a gauge going blank on one bad frame would
        // flicker constantly on a clone-grade adapter.
        Assert.Equal(60, snapshots[^1].Value(0x0D));
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_without_throwing()
    {
        var (_, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C]));

        // Safety net so a regression fails the test rather than hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = poller.RunAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        // Cancellation is the normal exit path — trips end by the car being
        // switched off, not by the loop deciding it is finished.
        await run;
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cancelling_before_the_first_cycle_does_nothing()
    {
        var (adapter, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C]));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await poller.RunAsync(cts.Token);

        Assert.Empty(adapter.CommandLog);
    }

    [Fact]
    public async Task An_empty_rotation_still_emits_snapshots()
    {
        // A car reporting no decodable PIDs must not spin the loop into a state
        // where the UI never updates and nothing explains why.
        var (_, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([]));

        var snapshots = await RunCycles(poller, 3);

        Assert.Equal(3, snapshots.Count);
        Assert.Empty(snapshots[^1].Readings);
    }

    [Fact]
    public async Task Reports_an_achieved_sample_rate()
    {
        var (_, session) = Connected();
        var poller = new ObdPoller(session, SupportedPids.FromPids([0x0C, 0x0D]),
            new PollerOptions { CycleDelay = TimeSpan.FromMilliseconds(10) });

        var snapshots = new List<ObdSnapshot>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        poller.SnapshotUpdated += (_, s) =>
        {
            snapshots.Add(s);
            if (s.SamplesPerSecond > 0)
                cts.Cancel();
        };

        await poller.RunAsync(cts.Token);

        // The roadmap wants this on screen: it is the fastest way to tell a
        // struggling adapter from a struggling app.
        Assert.True(snapshots[^1].SamplesPerSecond > 0);
    }
}
