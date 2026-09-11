namespace Motorcito.App.Controls;

/// <summary>
/// Pure geometry and colour maths for the arc gauge.
///
/// Split out from the drawable deliberately: none of this needs an
/// <see cref="ICanvas"/>, so it can be reasoned about — and later pinned by
/// tests — without standing up a MAUI rendering context.
/// </summary>
public static class GaugeGeometry
{
    /// <summary>Where the arc begins: bottom-left, 225° counter-clockwise from 3 o'clock.</summary>
    public const float StartAngle = 225f;

    /// <summary>Total travel, ending at bottom-right (-45°). A 270° sweep leaves
    /// the bottom of the dial open, which is where the label sits.</summary>
    public const float SweepAngle = 270f;

    /// <summary>
    /// The angle, in Microsoft.Maui.Graphics degrees, at fraction
    /// <paramref name="t"/> along the sweep. t=0 → 225°, t=1 → -45°.
    /// </summary>
    public static float AngleAt(double t) => StartAngle - (float)(Math.Clamp(t, 0, 1) * SweepAngle);

    /// <summary>Normalises a value into 0..1 across the gauge's range.</summary>
    public static double Fraction(double value, double min, double max)
        => max <= min ? 0 : Math.Clamp((value - min) / (max - min), 0, 1);

    /// <summary>
    /// Converts a gauge angle to a point on a circle of radius
    /// <paramref name="r"/> about (<paramref name="cx"/>, <paramref name="cy"/>).
    ///
    /// Y is subtracted rather than added: screen coordinates grow downward,
    /// but the angle convention treats 90° as up.
    /// </summary>
    public static (float X, float Y) PointOn(float cx, float cy, float r, float angleDegrees)
    {
        var rad = angleDegrees * MathF.PI / 180f;
        return (cx + r * MathF.Cos(rad), cy - r * MathF.Sin(rad));
    }

    /// <summary>
    /// The colour for a reading: accent below <paramref name="warn"/>, ramping
    /// to amber between warn and <paramref name="redline"/>, red at or above it.
    ///
    /// A gauge with no thresholds stays accent-coloured for its whole travel —
    /// most parameters have no meaningful "too high".
    /// </summary>
    public static Color ColorFor(double value, double? warn, double? redline)
    {
        if (redline is { } red && value >= red)
            return MotorcitoPalette.Redline;

        if (warn is not { } w)
            return MotorcitoPalette.Accent;

        if (value <= w)
            return MotorcitoPalette.Accent;

        // Between warn and redline, fade accent → amber. With no redline
        // defined, treat the top of the warning band as one unit away so the
        // colour still moves rather than snapping.
        var span = (redline ?? (w + 1)) - w;
        return MotorcitoPalette.Lerp(MotorcitoPalette.Accent, MotorcitoPalette.Warn, (value - w) / span);
    }
}
