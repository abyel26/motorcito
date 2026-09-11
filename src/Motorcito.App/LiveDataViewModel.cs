using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Motorcito.Data;
using Motorcito.Obd;

namespace Motorcito.App;

/// <summary>One gauge row: a parameter name and its current formatted value.</summary>
public sealed class GaugeItem : INotifyPropertyChanged
{
    private string _value = "—";

    public required byte Pid { get; init; }
    public required string Name { get; init; }
    public required string Unit { get; init; }

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
    private bool _isBusy;

    public ObservableCollection<GaugeItem> Gauges { get; } = [];

    public LiveDataViewModel(IObdAdapter adapter, LoggingService logging, GeolocationAltitudeProvider altitude)
    {
        _adapter = adapter;
        _logging = logging;
        _altitude = altitude;
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

            var poller = new ObdPoller(session, supported);
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
            Gauges.Add(new GaugeItem
            {
                Pid = def.Pid,
                Name = def.Name,
                Unit = def.Unit
            });
        }
    }

    private void OnSnapshot(object? sender, ObdSnapshot snapshot)
    {
        // Persist on the poller's thread rather than the UI thread: batched
        // inserts must not be gated on the main thread being free, and a slow
        // write must never stutter the gauges.
        _logging.Record(snapshot);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var gauge in Gauges)
            {
                gauge.Value = snapshot.Readings.TryGetValue(gauge.Pid, out var reading)
                    ? $"{reading.Value:0.##} {reading.Unit}"
                    : "—";
            }

            SampleRate = snapshot.SamplesPerSecond > 0
                ? $"{snapshot.SamplesPerSecond:0.#} reads/s"
                : "measuring…";
        });
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
