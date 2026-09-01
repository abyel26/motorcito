using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private bool _isBusy;

    public ObservableCollection<GaugeItem> Gauges { get; } = [];

    public LiveDataViewModel(IObdAdapter adapter)
    {
        _adapter = adapter;
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

            // CLAUDE.md rule 4: query capabilities at every connection and build
            // the gauge layout from the result. Never assume a PID exists.
            var supported = await session.ScanSupportedPidsAsync();
            CapabilitySummary = $"{supported.Count} PIDs reported, {supported.KnownSupported().Count()} decodable";

            EvaluateGate(supported);

            Vin = await session.ReadVinAsync() ?? "unavailable";

            var codes = await session.ReadDtcsAsync(DtcMode.Stored);
            DtcSummary = codes.Count == 0 ? "None stored" : string.Join(", ", codes);

            BuildGauges(supported);

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

        await _adapter.DisconnectAsync();
        Gauges.Clear();
        SampleRate = "—";
        OnPropertyChanged(nameof(IsConnected));
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
