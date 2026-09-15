namespace Motorcito.App.Controls;

/// <summary>
/// A sweep-arc gauge.
///
/// Deliberately does NOT redraw when <see cref="Value"/> changes. The poll loop
/// produces 5–20 snapshots a second; invalidating on each one, across a screen
/// of gauges, saturates the main thread and the numerals become unreadable.
/// Instead <see cref="Value"/> only moves the easing target, and a single
/// page-owned ticker calls <see cref="Tick"/> at a fixed frame rate.
/// </summary>
public sealed class GaugeView : GraphicsView
{
    private double _displayed;
    private bool _hasValue;

    public GaugeView()
    {
        Drawable = new ArcGaugeDrawable(this);
    }

    /// <summary>The eased value actually on screen. Lags <see cref="Value"/>.</summary>
    public double Displayed => _displayed;

    /// <summary>False until the first reading, so the gauge shows "—" rather than a confident 0.</summary>
    public bool HasValue => _hasValue;

    /// <summary>Highest value seen since the last <see cref="Reset"/>.</summary>
    public double? Peak { get; private set; }

    /// <summary>
    /// True when the PID was missing from the most recent snapshot. The gauge
    /// holds its last reading and dims rather than dropping to zero.
    /// </summary>
    public bool IsStale { get; private set; }

    /// <summary>
    /// True when connected to a car that does not report this parameter.
    ///
    /// Distinct from simply having no value yet: the gauges are always on
    /// screen, so "waiting for a connection" and "this car cannot give you
    /// this" must not look the same.
    /// </summary>
    public bool IsUnsupported { get; private set; }

    /// <summary>Marks whether the connected car reports this parameter at all.</summary>
    public void SetUnsupported(bool unsupported)
    {
        if (IsUnsupported == unsupported)
            return;

        IsUnsupported = unsupported;
        Invalidate();
    }

    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(double), typeof(GaugeView), 0d,
        propertyChanged: OnTargetChanged);

    /// <summary>
    /// The target reading. Setting this does not redraw — see the class remarks.
    /// </summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly BindableProperty MinProperty = BindableProperty.Create(
        nameof(Min), typeof(double), typeof(GaugeView), 0d, propertyChanged: Redraw);

    public double Min
    {
        get => (double)GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    public static readonly BindableProperty MaxProperty = BindableProperty.Create(
        nameof(Max), typeof(double), typeof(GaugeView), 100d, propertyChanged: Redraw);

    public double Max
    {
        get => (double)GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public static readonly BindableProperty WarnProperty = BindableProperty.Create(
        nameof(Warn), typeof(double?), typeof(GaugeView), null, propertyChanged: Redraw);

    /// <summary>Where the colour ramp starts leaving the normal range. Null for parameters with no meaningful limit.</summary>
    public double? Warn
    {
        get => (double?)GetValue(WarnProperty);
        set => SetValue(WarnProperty, value);
    }

    public static readonly BindableProperty RedlineProperty = BindableProperty.Create(
        nameof(Redline), typeof(double?), typeof(GaugeView), null, propertyChanged: Redraw);

    public double? Redline
    {
        get => (double?)GetValue(RedlineProperty);
        set => SetValue(RedlineProperty, value);
    }

    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(GaugeView), string.Empty, propertyChanged: Redraw);

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly BindableProperty UnitProperty = BindableProperty.Create(
        nameof(Unit), typeof(string), typeof(GaugeView), string.Empty, propertyChanged: Redraw);

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public static readonly BindableProperty DecimalsProperty = BindableProperty.Create(
        nameof(Decimals), typeof(int), typeof(GaugeView), 0, propertyChanged: Redraw);

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public static readonly BindableProperty TimeConstantProperty = BindableProperty.Create(
        nameof(TimeConstant), typeof(double), typeof(GaugeView), 0.15d);

    /// <summary>
    /// Seconds for the needle to close ~63% of the gap to a new target. Tune by
    /// what the quantity physically does: RPM must feel connected to the
    /// throttle (~0.06s), a coolant gauge should glide and never twitch (~0.6s).
    /// </summary>
    public double TimeConstant
    {
        get => (double)GetValue(TimeConstantProperty);
        set => SetValue(TimeConstantProperty, value);
    }

    /// <summary>
    /// Publishes a real reading.
    ///
    /// Prefer this over assigning <see cref="Value"/> directly. A BindableProperty
    /// only raises propertyChanged when the value actually differs, so a genuine
    /// reading of exactly zero — a stationary car, a closed throttle — would
    /// never mark the gauge as having data and it would sit showing "—".
    /// </summary>
    public void SetReading(double value)
    {
        _hasValue = true;

        if (Peak is not { } peak || value > peak)
            Peak = value;

        Value = value;
    }

    private static void OnTargetChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var gauge = (GaugeView)bindable;
        gauge._hasValue = true;

        var v = (double)newValue;
        if (gauge.Peak is not { } peak || v > peak)
            gauge.Peak = v;
    }

    private static void Redraw(BindableObject bindable, object oldValue, object newValue)
        => ((GaugeView)bindable).Invalidate();

    /// <summary>
    /// Marks the reading as missing from the latest snapshot without changing
    /// <see cref="Value"/> — the needle stays where it was and dims.
    /// </summary>
    public void SetStale(bool stale)
    {
        if (IsStale == stale)
            return;

        IsStale = stale;
        Invalidate();
    }

    /// <summary>
    /// True once <see cref="Configure"/> has applied a style. Range and label
    /// properties each invalidate on change, so they are set once per
    /// connection rather than every frame.
    /// </summary>
    public bool IsConfigured { get; private set; }

    /// <summary>
    /// Applies the label, unit, range and easing for the parameter this gauge
    /// is showing. Keeps <see cref="GaugeStyles"/> the single source of truth
    /// rather than duplicating ranges into XAML, where the two would drift.
    /// </summary>
    public void Configure(string label, string unit, double min, double max,
        double? warn, double? redline, int decimals, double timeConstant)
    {
        Label = label;
        Unit = unit;
        Min = min;
        Max = max;
        Warn = warn;
        Redline = redline;
        Decimals = decimals;
        TimeConstant = timeConstant;
        IsConfigured = true;
    }

    /// <summary>Clears value, peak and easing state. Called on disconnect so a gauge cannot show a stale car's reading.</summary>
    public void Reset()
    {
        _displayed = 0;
        _hasValue = false;
        Peak = null;
        IsStale = false;
        IsUnsupported = false;
        Value = 0;
        Invalidate();
    }

    /// <summary>
    /// Advances the easing by <paramref name="dt"/> seconds and redraws if the
    /// needle actually moved.
    ///
    /// Exponential approach rather than a fixed step, so the motion is
    /// frame-rate independent: a dropped frame produces a proportionally larger
    /// step instead of a visible stall. Critically damped with no overshoot —
    /// overshoot reads as a toy, this should read as an instrument.
    /// </summary>
    public void Tick(double dt)
    {
        if (!_hasValue || dt <= 0)
            return;

        var target = Value;
        var gap = target - _displayed;
        var epsilon = Math.Max(Math.Abs(Max - Min) * 0.0005, 1e-4);

        if (Math.Abs(gap) < epsilon)
        {
            if (_displayed == target)
                return;

            _displayed = target;
            Invalidate();
            return;
        }

        var tau = Math.Max(TimeConstant, 0.001);
        _displayed += gap * (1.0 - Math.Exp(-dt / tau));
        Invalidate();
    }
}
