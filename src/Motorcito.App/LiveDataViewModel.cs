using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Motorcito.App.Controls;
using Motorcito.Data;
using Motorcito.Obd;
using Motorcito.Obd.Signals;

namespace Motorcito.App;

/// <summary>One parameter on the dashboard: its identity, its display range, and its current reading.</summary>
public sealed class GaugeItem : INotifyPropertyChanged
{
    private string _value = "—";

    public required byte Pid { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }

    /// <summary>Short enough to fit under a gauge. From <see cref="GaugeStyles"/>.</summary>
    public required string ShortLabel { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public double? Warn { get; init; }
    public double? Redline { get; init; }
    public int Decimals { get; init; }
    public double TimeConstant { get; init; }

    /// <summary>
    /// The latest numeric reading, or null before the first one.
    ///
    /// A plain property with no change notification, on purpose: this is
    /// <em>polled</em> by the render ticker rather than pushed. Raising an event
    /// per gauge per snapshot — up to 20 times a second — is the cost this
    /// design exists to avoid.
    /// </summary>
    public double? Numeric { get; set; }

    /// <summary>True when the PID was absent from the most recent snapshot.</summary>
    public bool IsStale { get; set; }

    /// <summary>Formatted for the compact list. Refreshed well below the frame rate.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Drives the live dashboard: connects, runs the capability scan, evaluates the
/// Phase 0 gate, and pumps the poller's snapshots into observable gauges.
///
/// Holds no transport knowledge — it is handed an <see cref="IObdAdapter"/> and
/// does not care whether that is a real MX+ or the simulator.
/// </summary>
public sealed class LiveDataViewModel : INotifyPropertyChanged
{
    private readonly IObdAdapter _adapter;
    private readonly LoggingService _logging;
    private readonly GeolocationAltitudeProvider _altitude;
    private readonly IReadOnlyList<ISignalProfileSource> _sources;
    private readonly HashSet<string> _verifiedSignalKeys = [];
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    private string _status = "Disconnected";
    private string _adapterName = "—";
    private string _vin = "—";
    private string _sampleRate = "—";
    private string _gateVerdict = "Not yet checked";
    private Color _gateColor = Colors.Gray;
    private string _capabilitySummary = "—";
    private string _dtcSummary = "—";
    private string _tripStatus = "Not recording";
    private string _diagnosticSummary = "—";
    private string _oilTempSource = "—";
    private string _signalAttribution = string.Empty;
    private bool _isBusy;
    private ObdSnapshot? _latest;

    public ObservableCollection<GaugeItem> Gauges { get; } = [];

    /// <param name="sources">
    /// Manufacturer-specific signal catalogs. May be empty: every car still gets
    /// standard OBD data, and a catalog can be removed without touching this type.
    /// </param>
    public LiveDataViewModel(
        IObdAdapter adapter,
        LoggingService logging,
        GeolocationAltitudeProvider altitude,
        IEnumerable<ISignalProfileSource> sources)
    {
        _adapter = adapter;
        _logging = logging;
        _altitude = altitude;
        _sources = sources.ToList();
        _adapter.StateChanged += (_, state) => MainThread.BeginInvokeOnMainThread(
            () => Status = state.ToString());
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string AdapterName { get => _adapterName; private set => Set(ref _adapterName, value); }
    public string Vin { get => _vin; private set => Set(ref _vin, value); }
    public string SampleRate { get => _sampleRate; private set => Set(ref _sampleRate, value); }
    public string GateVerdict { get => _gateVerdict; private set => Set(ref _gateVerdict, value); }
    public Color GateColor { get => _gateColor; private set => Set(ref _gateColor, value); }
    public string CapabilitySummary { get => _capabilitySummary; private set => Set(ref _capabilitySummary, value); }
    public string DtcSummary { get => _dtcSummary; private set => Set(ref _dtcSummary, value); }
    public string TripStatus { get => _tripStatus; private set => Set(ref _tripStatus, value); }
    public string DiagnosticSummary { get => _diagnosticSummary; private set => Set(ref _diagnosticSummary, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>Where oil temperature comes from on this car, or why it is unavailable.</summary>
    public string OilTempSource { get => _oilTempSource; private set => Set(ref _oilTempSource, value); }

    /// <summary>Credit required by the catalogs whose definitions are in use. Empty when none are.</summary>
    public string SignalAttribution
    {
        get => _signalAttribution;
        private set
        {
            Set(ref _signalAttribution, value);
            OnPropertyChanged(nameof(HasSignalAttribution));
        }
    }

    public bool HasSignalAttribution => SignalAttribution.Length > 0;

    /// <summary>Whether this car answered a manufacturer read for <paramref name="key"/> plausibly at connect.</summary>
    public bool IsSignalVerified(string key) => _verifiedSignalKeys.Contains(key);

    /// <summary>The latest verified manufacturer-specific value for a canonical key, if any.</summary>
    public double? ExtendedValue(string key) => Latest?.ExtendedValue(key);

    public bool IsConnected => _adapter.State == ObdConnectionState.Connected;

    /// <summary>
    /// The full connect sequence: link up, initialise the adapter, discover what
    /// the ECU actually reports, evaluate the Phase 0 gate, then start polling.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _adapter.ConnectAsync();
            AdapterName = _adapter.Name;

            var session = new Elm327Session(_adapter);

            var init = await session.InitializeAsync();
            if (init.Failed)
            {
                Status = "Adapter did not respond to initialisation";
                return;
            }

            // query capabilities at every connection and build
            // the gauge layout from the result. Never assume a PID exists.
            var supported = await session.ScanSupportedPidsAsync();
            CapabilitySummary = $"{supported.Count} PIDs reported, {supported.KnownSupported().Count()} decodable";

            EvaluateGate(supported);

            Vin = await session.ReadVinAsync() ?? "unavailable";

            var codes = await session.ReadDtcsAsync(DtcMode.Stored);
            DtcSummary = codes.Count == 0 ? "None stored" : string.Join(", ", codes);

            BuildGauges(supported);

            // Get a location fix before a trip can open. A fix takes seconds and
            // the trip must start the instant the engine runs, so it cannot be
            // fetched at trip start. Failure here never blocks logging.
            await _altitude.RefreshAsync();

            // Recording starts before polling, so no snapshot is produced that
            // has nowhere to go. The recorder itself waits for a running engine
            // before opening a trip.
            var recorder = _logging.StartRecording(Vin == "unavailable" ? null : Vin, supported);

            // Tie subsequent exchanges to this car. Init, the capability scan
            // and the VIN read have already happened by now and stay
            // unattributed, which is correct — they precede identification.
            _logging.ObdLog.VehicleId = _logging.CurrentVehicle?.VehicleId;
            recorder.TripStarted += (_, trip) => MainThread.BeginInvokeOnMainThread(
                () => TripStatus = $"Recording since {trip.StartedAt.ToLocalTime():HH:mm:ss}");
            recorder.TripEnded += (_, trip) =>
            {
                // Refresh for the next trip: a stop-and-continue journey may
                // resume somewhere materially higher or lower.
                _ = _altitude.RefreshAsync();
                MainThread.BeginInvokeOnMainThread(() => TripStatus = FormatEndedTrip(trip));
            };

            // Before polling starts, so the probe has the adapter to itself.
            var extended = await ProbeGapsAsync(session, supported);

            var poller = new ObdPoller(session, supported, extendedCommands: extended);
            poller.SnapshotUpdated += OnSnapshot;

            _pollingCts = new CancellationTokenSource();
            _pollingTask = poller.RunAsync(_pollingCts.Token);
        }
        catch (ObdException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    /// <summary>
    /// Fills gaps standard OBD leaves on this car with manufacturer-specific
    /// reads — but only reads this car has just answered with plausible values.
    ///
    /// Today the one gap a hero gauge needs filled is oil temperature. The
    /// per-signal diagnostics card, and the rest of the vocabulary, arrive with
    /// the full catalog work.
    /// </summary>
    private async Task<IReadOnlyList<SignalCommand>> ProbeGapsAsync(Elm327Session session, SupportedPids supported)
    {
        _verifiedSignalKeys.Clear();
        SignalAttribution = string.Empty;

        if (supported.IsSupported(0x5C))
        {
            OilTempSource = "Standard OBD";
            return [];
        }

        // Make and year from the VIN when the car returns one. Without it every
        // catalog definition is a candidate; read-only requests make a wrong
        // guess cost a refusal, and the car's answers decide.
        var vin = VinInfo.Parse(Vin);
        var identity = new VehicleIdentity(vin?.Vin, vin?.Make, null, vin?.ModelYear);

        var candidates = _sources
            .SelectMany(s => s.CommandsFor(identity))
            .Where(c => c.Signals.Any(s => s.CanonicalKey == CanonicalSignals.OilTemp.Key))
            .ToList();

        if (candidates.Count == 0)
        {
            OilTempSource = "Not available for this car";
            return [];
        }

        var results = await SignalProbe.ProbeAsync(session, candidates);
        var verified = results.FirstOrDefault(r => r.State == ProbeState.Verified);

        if (verified is null)
        {
            OilTempSource = $"Not available — {results[0].Describe()}";
            return [];
        }

        foreach (var signal in verified.Signals)
        {
            if (signal.Reading is { } reading)
                _verifiedSignalKeys.Add(reading.Key);
        }

        OilTempSource = $"Manufacturer read ({verified.Command})";
        SignalAttribution = _sources.FirstOrDefault(s => s.SourceId == verified.Command.SourceId)?.Attribution ?? string.Empty;

        // One source per value: a second verified definition would only cost
        // another round trip for the same number.
        return [verified.Command];
    }

    public async Task DisconnectAsync()
    {
        if (_pollingCts is not null)
        {
            await _pollingCts.CancelAsync();
            try
            {
                if (_pollingTask is not null)
                    await _pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the poll loop ends.
            }

            _pollingCts.Dispose();
            _pollingCts = null;
            _pollingTask = null;
        }

        // Close the trip before dropping the link, so the recorder's own
        // last-sample timestamp is still the truth about when driving stopped.
        _logging.StopRecording();

        await _adapter.DisconnectAsync();
        Gauges.Clear();

        // Drop the last snapshot too, or the render ticker keeps pumping a
        // disconnected car's final readings into the gauges.
        Volatile.Write(ref _latest, null);

        // Verification is per connection: the next car may not answer the same reads.
        _verifiedSignalKeys.Clear();

        SampleRate = "—";
        OnPropertyChanged(nameof(IsConnected));
    }

    /// <summary>
    /// Writes buffered samples without ending the trip.
    ///
    /// Called when the app is backgrounded or about to be terminated: iOS can
    /// kill a background app without warning, and unflushed rows live only in
    /// memory. The trip stays open deliberately — being backgrounded is not the
    /// same as the drive ending, and orphan recovery handles the case where the
    /// app never comes back.
    /// </summary>
    public void FlushForLifecycleEvent()
    {
        var written = _logging.Flush();
        if (written > 0)
            System.Diagnostics.Debug.WriteLine($"[motorcito] flushed {written} sample(s) on lifecycle event");
    }

    /// <summary>
    /// Writes a database snapshot to a temporary file and hands it to the
    /// system share sheet — AirDrop, Files, mail, anything.
    ///
    /// The export goes to the cache directory rather than app data: it is a
    /// derived copy, and leaving snapshots in backed-up storage would duplicate
    /// the whole database on every share.
    /// </summary>
    public async Task ExportDatabaseAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var name = $"motorcito-{DateTime.Now:yyyyMMdd-HHmmss}.db";
            var path = Path.Combine(FileSystem.CacheDirectory, name);

            await Task.Run(() => _logging.ExportDatabase(path));

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Motorcito database",
                File = new ShareFile(path)
            });
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// When on, every adapter exchange is recorded rather than only one-off
    /// commands and failures. Expensive at 10–20 reads a second, so it is
    /// intended for minutes of active debugging, not for normal driving.
    /// </summary>
    public bool VerboseLogging
    {
        get => _adapter is LoggingObdAdapter { Mode: ObdLogMode.Verbose };
        set
        {
            if (_adapter is LoggingObdAdapter logging)
                logging.Mode = value ? ObdLogMode.Verbose : ObdLogMode.Diagnostic;

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Exports the raw adapter exchange log.
    ///
    /// Separate from the data exports because it answers a different question —
    /// what the adapter actually said, rather than what the car was doing — and
    /// because it contains raw replies including the VIN.
    /// </summary>
    public async Task ExportDiagnosticLogAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var path = Path.Combine(FileSystem.CacheDirectory,
                $"motorcito-obdlog-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var (file, rows) = await Task.Run(() => _logging.ExportDiagnosticLog(path));
            DiagnosticSummary = $"{rows} exchange(s) recorded";

            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Motorcito adapter log",
                File = new ShareFile(file)
            });
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Exports trips and samples as CSV, for starting in a spreadsheet.
    ///
    /// Shared as multiple files rather than zipped: iOS handles a multi-file
    /// share natively, and a zip would need unpacking before anything could
    /// read it.
    /// </summary>
    public async Task ExportCsvAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var directory = Path.Combine(FileSystem.CacheDirectory, $"csv-{DateTime.Now:yyyyMMdd-HHmmss}");
            var files = await Task.Run(() => _logging.ExportCsv(directory));

            await Share.Default.RequestAsync(new ShareMultipleFilesRequest
            {
                Title = "Motorcito data (CSV)",
                Files = [.. files.Select(f => new ShareFile(f))]
            });
        }
        catch (Exception ex)
        {
            Status = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatEndedTrip(Trip trip)
    {
        var duration = TimeSpan.FromSeconds(trip.DurationS ?? 0);
        var distance = trip.DistanceKm is { } km ? $", {km:0.0} km" : string.Empty;
        return $"Trip saved — {duration:hh\\:mm\\:ss}{distance}";
    }

    /// <summary>
    /// Surfaces the ROADMAP Phase 0 gate on screen rather than burying it in a
    /// log. If fuel trims are absent the detection plan changes fundamentally,
    /// so this is the single most important thing the app currently reports.
    /// </summary>
    private void EvaluateGate(SupportedPids supported)
    {
        var gate = supported.EvaluatePhaseZeroGate();

        if (gate.ScanLooksImplausible)
        {
            GateVerdict = "Scan failed — none of the near-universal PIDs were reported. Treat as a parse or link problem, not a limited vehicle.";
            GateColor = Colors.Orange;
            return;
        }

        if (gate.Passed)
        {
            GateVerdict = gate.O2Present
                ? "PASS — fuel trims (06/07) and O2 (14) present."
                : "PASS — fuel trims present. O2 (14) absent, so the lazy-sensor rule will not run.";
            GateColor = Colors.SeaGreen;
            return;
        }

        var missing = string.Join(", ", gate.Missing.Select(p => $"{p:X2}"));
        GateVerdict = $"FAIL — missing {missing}. Stop and re-plan before Phase 1.";
        GateColor = Colors.IndianRed;
    }

    private void BuildGauges(SupportedPids supported)
    {
        Gauges.Clear();
        foreach (var def in supported.KnownSupported().OrderBy(d => d.Poll).ThenBy(d => d.Pid))
        {
            var style = GaugeStyles.For(def);

            Gauges.Add(new GaugeItem
            {
                Pid = def.Pid,
                Name = def.Name,
                Unit = def.Unit,
                ShortLabel = style.ShortLabel,
                Min = style.Min,
                Max = style.Max,
                Warn = style.Warn,
                Redline = style.Redline,
                Decimals = style.Decimals,
                TimeConstant = style.TimeConstant
            });
        }

    }

    private void OnSnapshot(object? sender, ObdSnapshot snapshot)
    {
        // Persist on the poller's thread rather than the UI thread: batched
        // inserts must not be gated on the main thread being free, and a slow
        // write must never stutter the gauges.
        _logging.Record(snapshot);

        // Deliberately does not touch the UI. The poller produces 5–20
        // snapshots a second; dispatching each one to the main thread and
        // walking every gauge was affordable for a list of labels, but not once
        // each gauge also wants to redraw. The page pulls from Latest at a
        // fixed frame rate instead — see MainPage.OnTick.
        //
        // Safe to publish from this thread: ObdSnapshot is an immutable record
        // over an already-copied dictionary, so this is a reference swap.
        Volatile.Write(ref _latest, snapshot);
    }

    /// <summary>
    /// The most recent poll cycle, or null before the first one. Readable from
    /// any thread.
    /// </summary>
    public ObdSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>
    /// Pushes the latest readings into the gauge models. Called by the page's
    /// render ticker, on the main thread.
    /// </summary>
    /// <param name="refreshText">
    /// Whether to rebuild the formatted strings for the compact list. Those are
    /// throttled well below the frame rate: numerals changing 60 times a second
    /// are unreadable, and each one costs a string allocation and a layout pass.
    /// </param>
    public void PumpGauges(bool refreshText)
    {
        var snapshot = Latest;
        if (snapshot is null)
            return;

        foreach (var gauge in Gauges)
        {
            if (snapshot.Readings.TryGetValue(gauge.Pid, out var reading))
            {
                gauge.Numeric = reading.Value;
                gauge.IsStale = false;
            }
            else
            {
                // Hold the last reading. A cheap adapter drops frames
                // constantly, and a gauge falling to zero every few seconds
                // looks like a fault in the app.
                gauge.IsStale = true;
            }

            if (refreshText)
            {
                gauge.Value = gauge.Numeric is { } v
                    ? $"{v:0.##} {gauge.Unit}"
                    : "—";
            }
        }

        if (refreshText)
        {
            SampleRate = snapshot.SamplesPerSecond > 0
                ? $"{snapshot.SamplesPerSecond:0.#} reads/s"
                : "measuring…";
        }
    }

    /// <summary>The gauge for a PID, or null if this car does not report it.</summary>
    public GaugeItem? GaugeFor(byte pid)
    {
        foreach (var gauge in Gauges)
        {
            if (gauge.Pid == pid)
                return gauge;
        }

        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
