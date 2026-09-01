using System.Diagnostics;

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
}

/// <summary>A snapshot of every parameter read so far, replaced wholesale on each cycle.</summary>
public sealed record ObdSnapshot(
    DateTime TimestampUtc,
    IReadOnlyDictionary<byte, PidReading> Readings,
    double SamplesPerSecond)
{
    public double? Value(byte pid) => Readings.TryGetValue(pid, out var r) ? r.Value : null;
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

    /// <summary>Raised after each completed cycle with the current values.</summary>
    public event EventHandler<ObdSnapshot>? SnapshotUpdated;

    /// <summary>PIDs abandoned because they repeatedly failed despite being advertised as supported.</summary>
    public IReadOnlySet<byte> DroppedPids => _dropped;

    public ObdPoller(Elm327Session session, SupportedPids supported, PollerOptions? options = null)
    {
        _session = session;
        _options = options ?? new PollerOptions();

        // Fast parameters first so they are freshest when a snapshot is emitted.
        _rotation = supported.KnownSupported()
            .OrderBy(d => d.Poll)
            .ToList();
    }

    /// <summary>The PIDs this poller will actually read, in cycle order.</summary>
    public IReadOnlyList<PidDefinition> Rotation => _rotation;

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
                samplesPerSecond));

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
}
