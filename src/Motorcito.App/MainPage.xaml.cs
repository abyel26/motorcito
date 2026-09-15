using System.Diagnostics;
using Motorcito.App.Controls;

namespace Motorcito.App;

public partial class MainPage : ContentPage
{
    /// <summary>
    /// ~30 fps. The easing is frame-rate independent, so this is purely a
    /// smoothness/cost tradeoff. 30 is indistinguishable from 60 for a needle
    /// sweep and halves the main-thread work — which matters because the same
    /// device is holding a Bluetooth session and batching inserts to SQLite for
    /// the length of a drive.
    /// </summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// Refresh the formatted text every Nth frame — roughly 7 Hz at 30 fps.
    /// Numerals changing every frame are unreadable, and each one costs a
    /// string allocation plus a layout pass across the whole list.
    /// </summary>
    private const int TextEveryNFrames = 4;

    private readonly LiveDataViewModel _viewModel;
    private IDispatcherTimer? _ticker;
    private long _lastTimestamp;
    private int _frame;

    public MainPage(LiveDataViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // The gauges render before any connection, so they cannot wait for the
        // capability scan to tell them their own label and range.
        foreach (var (gauge, pid) in Heroes)
        {
            if (!gauge.IsConfigured)
                Configure(gauge, pid);
        }

        _ticker = Dispatcher.CreateTimer();
        _ticker.Interval = FrameInterval;
        _ticker.Tick += OnTick;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _ticker.Start();
    }

    protected override void OnDisappearing()
    {
        // A render timer running while the page is off screen is pure battery
        // cost, and this app is expected to keep logging for a whole drive.
        if (_ticker is not null)
        {
            _ticker.Stop();
            _ticker.Tick -= OnTick;
            _ticker = null;
        }

        base.OnDisappearing();
    }

    /// <summary>
    /// The single render pump. Pulls the newest snapshot, advances each gauge's
    /// easing, and lets the gauges decide whether they actually need redrawing.
    /// </summary>
    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        var refreshText = _frame++ % TextEveryNFrames == 0;
        _viewModel.PumpGauges(refreshText);

        foreach (var (gauge, pid) in Heroes)
        {
            Apply(gauge, pid);
            gauge.Tick(dt);
        }
    }

    /// <summary>The hero gauges and the PID each one shows.</summary>
    private IEnumerable<(GaugeView Gauge, byte Pid)> Heroes
    {
        get
        {
            yield return (RpmGauge, 0x0C);
            yield return (SpeedGauge, 0x0D);
            yield return (CoolantGauge, 0x05);
            yield return (OilGauge, 0x5C);
        }
    }

    /// <summary>Applies a gauge's label, unit and range from the style table.</summary>
    private static void Configure(GaugeView gauge, byte pid)
    {
        var style = GaugeStyles.For(pid);
        gauge.Configure(style.ShortLabel, GaugeStyles.UnitFor(pid), style.Min, style.Max,
            style.Warn, style.Redline, style.Decimals, style.TimeConstant);
    }

    /// <summary>Copies one PID's reading from the view model onto a hero gauge.</summary>
    private void Apply(GaugeView gauge, byte pid)
    {
        var item = _viewModel.GaugeFor(pid);

        if (item is null)
        {
            // No gauge model means either no connection yet, or a connected car
            // whose capability scan did not report this PID. Those are
            // different facts and the dial must not present them identically.
            gauge.SetUnsupported(_viewModel.IsConnected);
            return;
        }

        gauge.SetUnsupported(false);

        if (item.Numeric is not { } value)
        {
            gauge.SetStale(true);
            return;
        }

        gauge.SetReading(value);
        gauge.SetStale(item.IsStale);
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
        => await _viewModel.ConnectAsync();

    private async void OnDisconnectClicked(object? sender, EventArgs e)
    {
        await _viewModel.DisconnectAsync();

        foreach (var (gauge, _) in Heroes)
            gauge.Reset();
    }

    private async void OnExportDatabaseClicked(object? sender, EventArgs e)
        => await _viewModel.ExportDatabaseAsync();

    private async void OnExportCsvClicked(object? sender, EventArgs e)
        => await _viewModel.ExportCsvAsync();

    private async void OnExportDiagnosticLogClicked(object? sender, EventArgs e)
        => await _viewModel.ExportDiagnosticLogAsync();
}
