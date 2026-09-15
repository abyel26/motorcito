using Motorcito.Obd;

namespace Motorcito.App.Controls;

/// <summary>How one parameter should be displayed.</summary>
/// <param name="ShortLabel">Fits under a gauge; the registry's full name does not.</param>
/// <param name="Min">Bottom of the sweep.</param>
/// <param name="Max">Top of the sweep.</param>
/// <param name="Warn">Where the colour ramp leaves the normal range, or null if there is no meaningful limit.</param>
/// <param name="Redline">Where it goes red.</param>
/// <param name="Decimals">Digits after the point in the numeral.</param>
/// <param name="TimeConstant">Needle easing, in seconds. See <see cref="GaugeView.TimeConstant"/>.</param>
public sealed record GaugeStyle(
    string ShortLabel,
    double Min,
    double Max,
    double? Warn = null,
    double? Redline = null,
    int Decimals = 0,
    double TimeConstant = 0.15);

/// <summary>
/// Display metadata per PID.
///
/// This lives in the app rather than on <see cref="PidDefinition"/> for two
/// reasons. Motorcito.Obd is deliberately free of display concerns — it is the
/// library the whole test suite runs through with no hardware and no MAUI. And
/// more practically, these numbers are <em>car</em>-specific rather than
/// PID-specific: an MX-5 redlines around 6800, a G80 M3 at 7200. A single value
/// on a shared PID definition would be wrong for one of them. Keeping the table
/// here means it can become per-vehicle later without touching the protocol
/// layer.
/// </summary>
public static class GaugeStyles
{
    // Ranges are chosen so a normal reading sits in the middle of the sweep,
    // not pinned at one end — a gauge that never moves is decoration.
    //
    // NOTE: the RPM redline is for a 2018 ND1 MX-5 (2.0 Skyactiv-G). The 2019+
    // ND2 revs to 7500. Verify against the car's own tachometer rather than
    // trusting this number.
    private static readonly Dictionary<byte, GaugeStyle> ByPid = new()
    {
        [0x0C] = new("RPM", 0, 7000, Warn: 6000, Redline: 6800, TimeConstant: 0.06),
        [0x0D] = new("SPEED", 0, 200, TimeConstant: 0.15),
        // Temperature gauges start at 0, not at operating temperature. A cold
        // start is a real reading — the simulator begins coolant at 20 °C and a
        // winter morning is lower still — and a gauge pinned empty for the
        // first ten minutes of every drive looks broken. The thermostat rule
        // also cares specifically about the warm-up ramp.
        [0x05] = new("COOLANT", 0, 130, Warn: 105, Redline: 115, TimeConstant: 0.6),
        [0x5C] = new("OIL TEMP", 0, 150, Warn: 125, Redline: 140, TimeConstant: 0.6),
        [0x11] = new("THROTTLE", 0, 100, TimeConstant: 0.05),
        [0x04] = new("LOAD", 0, 100, TimeConstant: 0.1),
        [0x43] = new("ABS LOAD", 0, 100, TimeConstant: 0.1),
        [0x0B] = new("MAP", 0, 255, TimeConstant: 0.1),
        [0x10] = new("MAF", 0, 150, Decimals: 1, TimeConstant: 0.1),
        [0x0F] = new("INTAKE", -20, 80, TimeConstant: 0.6),
        [0x46] = new("AMBIENT", -20, 50, TimeConstant: 0.6),
        [0x0E] = new("TIMING", -30, 60, TimeConstant: 0.2),
        [0x06] = new("STFT B1", -25, 25, Decimals: 1, TimeConstant: 0.3),
        [0x07] = new("LTFT B1", -25, 25, Decimals: 1, TimeConstant: 0.5),
        [0x08] = new("STFT B2", -25, 25, Decimals: 1, TimeConstant: 0.3),
        [0x09] = new("LTFT B2", -25, 25, Decimals: 1, TimeConstant: 0.5),
        [0x14] = new("O2 S1", 0, 1.275, Decimals: 2, TimeConstant: 0.05),
        [0x15] = new("O2 S2", 0, 1.275, Decimals: 2, TimeConstant: 0.05),
        [0x2F] = new("FUEL", 0, 100, TimeConstant: 0.8),
        [0x42] = new("BATTERY", 8, 16, Decimals: 1, TimeConstant: 0.4),
        [0x44] = new("LAMBDA", 0.7, 1.3, Decimals: 3, TimeConstant: 0.1),
        [0x5E] = new("FUEL RATE", 0, 30, Decimals: 1, TimeConstant: 0.2),
        [0x1F] = new("RUN TIME", 0, 3600, TimeConstant: 1.0),
        [0x21] = new("MIL DIST", 0, 1000, TimeConstant: 1.0),
    };

    /// <summary>
    /// The style for a PID. Falls back to a plausible range derived from the
    /// unit, so a PID added to the registry without a style entry still renders
    /// something sensible rather than a gauge pinned at one end.
    /// </summary>
    public static GaugeStyle For(PidDefinition definition)
        => ByPid.TryGetValue(definition.Pid, out var style) ? style : Fallback(definition);

    /// <summary>
    /// The style for a PID by number, without needing a capability scan.
    ///
    /// The hero gauges render before any connection exists, so they cannot wait
    /// for the car to tell them their own range.
    /// </summary>
    public static GaugeStyle For(byte pid)
    {
        if (ByPid.TryGetValue(pid, out var style))
            return style;

        var definition = PidRegistry.Find(pid);
        return definition is null
            ? new GaugeStyle($"PID {pid:X2}", 0, 100, Decimals: 1)
            : Fallback(definition);
    }

    /// <summary>The metric unit for a PID. Units are protocol facts, so they come from the registry.</summary>
    public static string UnitFor(byte pid) => PidRegistry.Find(pid)?.Unit ?? string.Empty;

    private static GaugeStyle Fallback(PidDefinition definition)
    {
        var label = definition.Name.ToUpperInvariant();

        return definition.Unit switch
        {
            "%" => new GaugeStyle(label, 0, 100),
            "°C" => new GaugeStyle(label, -40, 150, TimeConstant: 0.6),
            "kPa" => new GaugeStyle(label, 0, 255, TimeConstant: 0.2),
            "V" => new GaugeStyle(label, 0, 16, Decimals: 2),
            "rpm" => new GaugeStyle(label, 0, 8000, TimeConstant: 0.06),
            "km/h" => new GaugeStyle(label, 0, 250),
            "lambda" => new GaugeStyle(label, 0.7, 1.3, Decimals: 3),
            _ => new GaugeStyle(label, 0, 100, Decimals: 1),
        };
    }
}
