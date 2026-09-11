namespace Motorcito.App.Controls;

/// <summary>
/// The app's colours, in one place.
///
/// This is C#-first rather than XAML-first because an <see cref="IDrawable"/>
/// cannot cleanly resolve a StaticResource — the drawing code needs real
/// <see cref="Color"/> values. Resources/Styles/Colors.xaml mirrors the keys
/// that XAML needs; this type is the source of truth for both.
/// </summary>
public static class MotorcitoPalette
{
    /// <summary>Page background.</summary>
    public static readonly Color Page = Color.FromArgb("#0E1512");

    /// <summary>Card background, one step lighter than the page.</summary>
    public static readonly Color Card = Color.FromArgb("#16211C");

    /// <summary>Unfilled gauge track. Must read as a groove, not as a value.</summary>
    public static readonly Color Groove = Color.FromArgb("#1E2B24");

    /// <summary>Normal-range accent.</summary>
    public static readonly Color Accent = Color.FromArgb("#2D6A4F");

    /// <summary>Approaching the limit.</summary>
    public static readonly Color Warn = Color.FromArgb("#E0A458");

    /// <summary>At or past the limit.</summary>
    public static readonly Color Redline = Color.FromArgb("#B23A48");

    /// <summary>Primary readout text.</summary>
    public static readonly Color Text = Color.FromArgb("#FFFFFF");

    /// <summary>Labels and secondary values.</summary>
    public static readonly Color TextMuted = Color.FromArgb("#C8DCD0");

    /// <summary>Units and supporting detail.</summary>
    public static readonly Color TextDim = Color.FromArgb("#8FA99A");

    /// <summary>Section captions.</summary>
    public static readonly Color Caption = Color.FromArgb("#6E8B7E");

    public static readonly Color TickMajor = Color.FromArgb("#5A7A6A");
    public static readonly Color TickMinor = Color.FromArgb("#33473D");

    /// <summary>
    /// Blends <paramref name="from"/> toward <paramref name="to"/>.
    /// <see cref="Color.Lerp"/> does not exist in Microsoft.Maui.Graphics, so
    /// the gauge colour ramp needs this.
    /// </summary>
    public static Color Lerp(Color from, Color to, double t)
    {
        var k = (float)Math.Clamp(t, 0, 1);
        return new Color(
            from.Red + (to.Red - from.Red) * k,
            from.Green + (to.Green - from.Green) * k,
            from.Blue + (to.Blue - from.Blue) * k,
            from.Alpha + (to.Alpha - from.Alpha) * k);
    }
}
