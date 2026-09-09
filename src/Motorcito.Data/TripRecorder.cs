using Motorcito.Obd;

namespace Motorcito.Data;

public sealed class TripRecorderOptions
{
    /// <summary>Rows accumulated before a write. CLAUDE.md fixes this range at 100–500.</summary>
    public int BatchSize { get; init; } = 200;

    /// <summary>
    /// Flush even when the batch is not full, so a short trip is not lost and
    /// the trip list is not minutes stale.
    /// </summary>
    public TimeSpan MaxBatchAge { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>RPM must exceed this for the engine to count as running.</summary>
    public double RunningRpmThreshold { get; init; } = 50;

    /// <summary>
    /// How long RPM must stay at zero before a trip is considered over.
    ///
    /// Long on purpose. Auto start-stop cuts the engine at every red light, and
    /// lights routinely run 45–90 seconds, so a short value splits one drive
    /// into several. That matters because the sufficiency gate requires ten
    /// *distinct* trips — split drives inflate the count and let a bucket cross
    /// the gate on far less independent data than it appears to have.
    ///
    /// Erring long merges genuinely separate short errands instead, which
    /// under-counts trips. That is the safer direction: it makes the gate
    /// harder to cross, not easier.
    ///
    /// Costs nothing in accuracy because <c>ended_at</c> is backdated to the
    /// moment RPM hit zero, not the moment the wait expired.
    ///
    /// This is a placeholder. Once real drives are logged, pick it from the
    /// observed distribution of zero-RPM gaps: start-stop events cluster low,
    /// real parking sits far out, and the gap between them is the answer.
    /// </summary>
    public TimeSpan IdleBeforeTripEnd { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Coolant below this at trip start marks it a cold start, which several rules gate on.</summary>
    public double ColdStartCoolantC { get; init; } = 50;

    public TimeSpan BufferWindow { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Turns a stream of <see cref="ObdSnapshot"/> into persisted trips and samples.
///
/// Trip detection follows SCHEMA.md: start on connect with RPM above idle, end
/// on disconnect or sustained RPM zero. The engine stopping at a long traffic
/// light should not split one drive into two, hence the sustained requirement
/// rather than an instantaneous one.
/// </summary>
public sealed class TripRecorder
{
    private readonly TripRepository _trips;
    private readonly SampleRepository _samples;
    private readonly TripRecorderOptions _options;
    private readonly List<Sample> _pending = [];
    private readonly object _lock = new();

    private string? _tripId;
    private DateTime _tripStarted;
    private DateTime _lastFlush = DateTime.UtcNow;
    private DateTime? _rpmZeroSince;
    private double _distanceKm;
    private DateTime? _lastSampleAt;
    private double? _lastSpeedKph;

    public string UserId { get; }
    public string VehicleId { get; }

    /// <summary>Full-resolution recent history, for event capture.</summary>
    public RollingSampleBuffer Buffer { get; }

    /// <summary>The trip currently being recorded, or null between trips.</summary>
    public string? CurrentTripId { get { lock (_lock) return _tripId; } }

    public event EventHandler<Trip>? TripStarted;
    public event EventHandler<Trip>? TripEnded;

    public TripRecorder(
        TripRepository trips,
        SampleRepository samples,
        string userId,
        string vehicleId,
        TripRecorderOptions? options = null)
    {
        _trips = trips;
        _samples = samples;
        UserId = userId;
        VehicleId = vehicleId;
        _options = options ?? new TripRecorderOptions();
        Buffer = new RollingSampleBuffer(_options.BufferWindow);
    }

    /// <summary>
    /// Feed one poller snapshot. Starts a trip if the engine is running, records
    /// the sample, and ends the trip once RPM has been zero long enough.
    /// </summary>
    public void Record(ObdSnapshot snapshot)
    {
        lock (_lock)
        {
            var rpm = snapshot.Value(0x0C);
            var running = rpm is { } r && r > _options.RunningRpmThreshold;

            if (_tripId is null)
            {
                // Wait for the engine before opening a trip: ignition-on with the
                // engine off would otherwise create empty trips every time the
                // app connects.
                if (!running)
                    return;

                StartTrip(snapshot);
            }

            var sample = Sample.FromSnapshot(snapshot, _tripId!, VehicleId);
            _pending.Add(sample);
            Buffer.Add(sample);

            AccumulateDistance(sample);

            if (running)
            {
                _rpmZeroSince = null;
            }
            else
            {
                _rpmZeroSince ??= snapshot.TimestampUtc;
                if (snapshot.TimestampUtc - _rpmZeroSince >= _options.IdleBeforeTripEnd)
                {
                    // End at the moment the engine actually stopped, not the
                    // moment the wait expired. Otherwise every trip is inflated
                    // by exactly IdleBeforeTripEnd, and those trailing
                    // engine-off samples land in the idle/stationary buckets —
                    // the ones the vacuum-leak rule reads.
                    EndTrip(_rpmZeroSince.Value);
                    return;
                }
            }

            if (_pending.Count >= _options.BatchSize ||
                DateTime.UtcNow - _lastFlush >= _options.MaxBatchAge)
            {
                Flush();
            }
        }
    }

    /// <summary>
    /// Ends the trip because the link dropped. Cars get switched off; this is
    /// the normal way a drive finishes, not an error path.
    /// </summary>
    public void OnDisconnected()
    {
        lock (_lock)
        {
            if (_tripId is not null)
                EndTrip(_lastSampleAt ?? DateTime.UtcNow);
        }
    }

    /// <summary>Writes any buffered rows. Safe to call at any time.</summary>
    public int Flush()
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
                return 0;

            var written = _samples.InsertBatch(_pending);
            _pending.Clear();
            _lastFlush = DateTime.UtcNow;
            return written;
        }
    }

    private void StartTrip(ObdSnapshot snapshot)
    {
        _tripId = Guid.NewGuid().ToString("N");
        _tripStarted = snapshot.TimestampUtc;
        _distanceKm = 0;
        _lastSampleAt = null;
        _lastSpeedKph = null;
        _rpmZeroSince = null;

        var coolant = snapshot.Value(0x05);

        var trip = new Trip
        {
            TripId = _tripId,
            VehicleId = VehicleId,
            UserId = UserId,
            StartedAt = _tripStarted,
            // IAT before the engine warms the intake is the best available
            // ambient reading; PID 46 is preferred when the car reports it.
            AmbientTempC = snapshot.Value(0x46) ?? snapshot.Value(0x0F),
            // Evaluated once, at trip start. A consequence of the long
            // IdleBeforeTripEnd is that two short errands can merge into one
            // trip; the merged trip then inherits the first segment's
            // cold-start flag. Harmless today, but the thermostat rule gates on
            // this, so revisit when that lands.
            WasColdStart = coolant is { } c && c < _options.ColdStartCoolantC,
        };

        _trips.Insert(trip);
        TripStarted?.Invoke(this, trip);
    }

    private void EndTrip(DateTime endedAt)
    {
        if (_tripId is null)
            return;

        Flush();

        var duration = (int)Math.Max(0, (endedAt - _tripStarted).TotalSeconds);
        _trips.Complete(_tripId, endedAt, _distanceKm > 0 ? _distanceKm : null, duration);

        var completed = _trips.Get(_tripId);
        _tripId = null;
        _rpmZeroSince = null;
        Buffer.Clear();

        if (completed is not null)
            TripEnded?.Invoke(this, completed);
    }

    /// <summary>
    /// Integrates speed over time.
    ///
    /// Trapezoidal rather than last-value: at a 1 Hz sample rate a
    /// last-value integration systematically over-reads during acceleration and
    /// under-reads during braking. Distance feeds fuel-economy buckets later,
    /// so a consistent bias would show up as a phantom trend.
    /// </summary>
    private void AccumulateDistance(Sample sample)
    {
        if (sample.SpeedKph is not { } speed)
            return;

        if (_lastSampleAt is { } last && _lastSpeedKph is { } lastSpeed)
            _distanceKm += DistanceCalculator.Step(lastSpeed, speed, sample.Timestamp - last);

        _lastSampleAt = sample.Timestamp;
        _lastSpeedKph = speed;
    }
}
