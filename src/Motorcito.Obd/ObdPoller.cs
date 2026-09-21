using System.Diagnostics;
using Motorcito.Obd.Signals;

namespace Motorcito.Obd;

public sealed class PollerOptions
{
    /// <summary>Poll <see cref="PollClass.Medium"/> parameters once every N cycles.</summary>
    public int MediumEveryNCycles { get; init; } = 4;

    /// <summary>Poll <see cref="PollClass.Slow"/> parameters once every N cycles.</summary>
    public int SlowEveryNCycles { get; init; } = 20;

    /// <summary>
    /// Consecutive failures on one PID before it is dropped from the rotation.
    ///
    /// A PID the capability scan claimed but the ECU will not actually answer
    /// otherwise burns a round trip every cycle, forever.
    /// </summary>
    public int FailuresBeforeDropping { get; init; } = 5;

    /// <summary>Idle time between cycles. Zero polls as fast as the adapter allows.</summary>
    public TimeSpan CycleDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Floor on how often a manufacturer-specific command is re-read, whatever
    /// its source suggests. Each one costs a header change on top of the read,
    /// and none of the values it carries change faster than this matters.
    /// </summary>
    public TimeSpan MinimumExtendedInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many manufacturer-specific reads one cycle may spend.
    ///
    /// They are not free: a batch costs its reads plus two header changes, and
    /// every one of those delays the next Mode 01 cycle. Polling a carful of
    /// them each cycle dropped the achieved rate from about 11 reads/s to 7 and
    /// put a visible lag in the RPM gauge. One per cycle keeps the dashboard
    /// responsive; anything due waits its turn, oldest first.
    /// </summary>
    public int MaxExtendedReadsPerCycle { get; init; } = 1;
}

/// <summary>A snapshot of every parameter read so far, replaced wholesale on each cycle.</summary>
/// <param name="Extended">Verified manufacturer-specific values, keyed by canonical key plus qualifier. Empty when none.</param>
public sealed record ObdSnapshot(
    DateTime TimestampUtc,
    IReadOnlyDictionary<byte, PidReading> Readings,
    double SamplesPerSecond,
    IReadOnlyDictionary<string, SignalReading>? Extended = null)
{
    public double? Value(byte pid) => Readings.TryGetValue(pid, out var r) ? r.Value : null;

    public double? ExtendedValue(string key)
        => Extended is not null && Extended.TryGetValue(key, out var r) ? r.Value : null;
}

/// <summary>
/// Drives the read loop: which PIDs to ask for, how often, and what to do when
/// one stops answering.
///
/// Frequency matters because every read is a round trip. On a clone at ~10
/// queries/sec, polling coolant as often as RPM costs most of the RPM
/// resolution; on the MX+ there is more headroom but the ordering still decides
/// how current the fast gauges look.
/// </summary>
public sealed class ObdPoller
{
    private readonly Elm327Session _session;
    private readonly PollerOptions _options;
    private readonly List<PidDefinition> _rotation;
    private readonly Dictionary<byte, PidReading> _latest = [];
    private readonly Dictionary<byte, int> _consecutiveFailures = [];
    private readonly HashSet<byte> _dropped = [];
    private readonly List<ExtendedState> _extended;
    private readonly Dictionary<string, SignalReading> _latestExtended = [];

    /// <summary>Raised after each completed cycle with the current values.</summary>
    public event EventHandler<ObdSnapshot>? SnapshotUpdated;

    /// <summary>PIDs abandoned because they repeatedly failed despite being advertised as supported.</summary>
    public IReadOnlySet<byte> DroppedPids => _dropped;

    /// <param name="extendedCommands">
    /// Manufacturer-specific commands to poll alongside Mode 01. Pass only
    /// commands this car has already answered plausibly — see <see cref="SignalProbe"/>.
    /// </param>
    public ObdPoller(
        Elm327Session session,
        SupportedPids supported,
        PollerOptions? options = null,
        IEnumerable<SignalCommand>? extendedCommands = null)
    {
        _session = session;
        _options = options ?? new PollerOptions();

        // Fast parameters first so they are freshest when a snapshot is emitted.
        _rotation = supported.KnownSupported()
            .OrderBy(d => d.Poll)
            .ToList();

        _extended = (extendedCommands ?? [])
            .Where(c => c.IsSendable)
            .Select(c => new ExtendedState(c, c.ToRequest(), IntervalFor(c)))
            .ToList();
    }

    /// <summary>The PIDs this poller will actually read, in cycle order.</summary>
    public IReadOnlyList<PidDefinition> Rotation => _rotation;

    /// <summary>Manufacturer-specific commands still being polled.</summary>
    public IReadOnlyList<SignalCommand> ExtendedCommands
        => _extended.Where(s => !s.Dropped).Select(s => s.Command).ToList();

    /// <summary>
    /// Runs until cancelled. Cancellation is the normal exit path — trips end by
    /// the car being switched off, not by the loop deciding it is finished.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cycle = 0L;
        var clock = Stopwatch.StartNew();
        var readsThisSecond = 0;
        var rateWindowStart = clock.Elapsed;
        var samplesPerSecond = 0.0;

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var def in _rotation)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (_dropped.Contains(def.Pid) || !IsDueThisCycle(def, cycle))
                    continue;

                DecodeResult result;
                try
                {
                    result = await _session.ReadAsync(def, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObdException)
                {
                    // Transport-level failure. Count it like a decode failure so
                    // a dead PID still drops out, but never let it kill the loop.
                    RecordFailure(def);
                    continue;
                }

                readsThisSecond++;

                if (result.Success)
                {
                    _latest[def.Pid] = result.Reading!;
                    _consecutiveFailures.Remove(def.Pid);
                }
                else
                {
                    RecordFailure(def);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
                readsThisSecond += await PollExtendedAsync(clock.Elapsed, cancellationToken).ConfigureAwait(false);

            // Recompute the achieved rate about once a second. The roadmap wants
            // this on screen: it is the fastest way to tell a struggling adapter
            // from a struggling app.
            var elapsed = clock.Elapsed - rateWindowStart;
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                samplesPerSecond = readsThisSecond / elapsed.TotalSeconds;
                readsThisSecond = 0;
                rateWindowStart = clock.Elapsed;
            }

            SnapshotUpdated?.Invoke(this, new ObdSnapshot(
                DateTime.UtcNow,
                new Dictionary<byte, PidReading>(_latest),
                samplesPerSecond,
                new Dictionary<string, SignalReading>(_latestExtended)));

            cycle++;

            if (_options.CycleDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(_options.CycleDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Reads whichever manufacturer-specific commands are due, grouped by
    /// header, then returns the adapter to the functional header so the next
    /// Mode 01 cycle reaches every ECU again.
    /// </summary>
    /// <returns>How many requests reached the adapter.</returns>
    private async Task<int> PollExtendedAsync(TimeSpan now, CancellationToken ct)
    {
        // Oldest due first, so a cycle's single slot rotates fairly, then
        // grouped by header so a batch of two costs one header change.
        var due = _extended
            .Where(s => !s.Dropped && s.NextDue <= now)
            .OrderBy(s => s.NextDue)
            .Take(Math.Max(1, _options.MaxExtendedReadsPerCycle))
            .OrderBy(s => s.Request.Header, StringComparer.Ordinal)
            .ToList();

        if (due.Count == 0)
            return 0;

        var reads = 0;
        try
        {
            foreach (var state in due)
            {
                if (ct.IsCancellationRequested)
                    break;

                state.NextDue = now + state.Interval;

                UdsResponse response;
                try
                {
                    response = await _session.ReadIdentifierAsync(state.Request, ct: ct).ConfigureAwait(false);
                }
                catch (ObdException)
                {
                    RecordFailure(state);
                    continue;
                }

                reads++;

                var plausible = SignalDecoder.Decode(state.Command, response)
                    .Where(r => r.Check == SignalCheck.Plausible)
                    .ToList();

                // A read that stops producing plausible values is treated like a
                // PID that stops answering: counted, then dropped. A value that
                // was sane at connect and is now impossible is not shown.
                if (plausible.Count == 0)
                {
                    RecordFailure(state);
                    continue;
                }

                state.Failures = 0;
                foreach (var result in plausible)
                    _latestExtended[result.Reading!.Key] = result.Reading;
            }

            await _session.RestoreFunctionalHeaderAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObdException)
        {
            // Restore failed: the session marks the header unknown, and the next
            // Mode 01 failures are counted like any other.
        }

        return reads;
    }

    /// <summary>
    /// How often a manufacturer-specific command is re-read: never faster than
    /// its source suggests, than the values it carries are worth, or than the
    /// poller's floor. A command carrying several values is paced by the one
    /// that needs refreshing soonest.
    /// </summary>
    private TimeSpan IntervalFor(SignalCommand command)
    {
        var signalFloor = command.Signals
            .Select(s => CanonicalSignals.Find(s.CanonicalKey)?.MinimumIntervalSeconds)
            .OfType<double>()
            .DefaultIfEmpty(0)
            .Min();

        var seconds = Math.Max(
            Math.Max(command.IntervalSeconds, signalFloor),
            _options.MinimumExtendedInterval.TotalSeconds);

        return TimeSpan.FromSeconds(seconds);
    }

    private bool IsDueThisCycle(PidDefinition def, long cycle) => def.Poll switch
    {
        PollClass.Fast => true,
        PollClass.Medium => cycle % _options.MediumEveryNCycles == 0,
        PollClass.Slow => cycle % _options.SlowEveryNCycles == 0,
        _ => true
    };

    private void RecordFailure(PidDefinition def)
    {
        var count = _consecutiveFailures.GetValueOrDefault(def.Pid) + 1;
        _consecutiveFailures[def.Pid] = count;

        if (count >= _options.FailuresBeforeDropping)
            _dropped.Add(def.Pid);
    }

    private void RecordFailure(ExtendedState state)
    {
        state.Failures++;
        if (state.Failures >= _options.FailuresBeforeDropping)
            state.Dropped = true;
    }

    private sealed class ExtendedState(SignalCommand command, SignalRequest request, TimeSpan interval)
    {
        public SignalCommand Command { get; } = command;
        public SignalRequest Request { get; } = request;
        public TimeSpan Interval { get; } = interval;
        public TimeSpan NextDue { get; set; } = TimeSpan.Zero;
        public int Failures { get; set; }
        public bool Dropped { get; set; }
    }
}
