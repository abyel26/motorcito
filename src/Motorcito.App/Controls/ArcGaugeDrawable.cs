// Microsoft.Maui.Font and Microsoft.Maui.Graphics.Font are both in scope via
// ImplicitUsings. Canvas text takes the Graphics one.
using GraphicsFont = Microsoft.Maui.Graphics.Font;

namespace Motorcito.App.Controls;

/// <summary>
/// Draws one sweep-arc gauge.
///
/// Holds a reference to its owning <see cref="GaugeView"/> rather than its own
/// state, so the drawable stays a pure function of (view properties, rect) and
/// there is only one copy of each value to keep in sync.
/// </summary>
public sealed class ArcGaugeDrawable(GaugeView owner) : IDrawable
{
    public void Draw(ICanvas canvas, RectF rect)
    {
        canvas.Antialias = true;

        var size = Math.Min(rect.Width, rect.Height);
        if (size <= 0)
            return;

        var stroke = size * 0.075f;
        var r = size / 2f - stroke;
        var cx = rect.Center.X;
        var cy = rect.Center.Y + size * 0.04f; // nudge down: the 270° arc is top-heavy
        var box = new RectF(cx - r, cy - r, r * 2, r * 2);

        var displayed = owner.Displayed;
        var t = GaugeGeometry.Fraction(displayed, owner.Min, owner.Max);
        var endAngle = GaugeGeometry.AngleAt(t);
        var accent = GaugeGeometry.ColorFor(displayed, owner.Warn, owner.Redline);

        canvas.StrokeLineCap = LineCap.Round;

        // 1. Track — the unfilled groove the value runs in.
        canvas.StrokeSize = stroke;
        canvas.StrokeColor = MotorcitoPalette.Groove;
        canvas.DrawArc(box, GaugeGeometry.StartAngle, GaugeGeometry.AngleAt(1), true, false);

        // 2. Redline zone, on the track and under the value arc, so the value
        //    covers it as it enters rather than the two fighting.
        if (owner.Redline is { } redline)
        {
            var redStart = GaugeGeometry.AngleAt(GaugeGeometry.Fraction(redline, owner.Min, owner.Max));
            canvas.StrokeColor = MotorcitoPalette.Redline.WithAlpha(0.32f);
            canvas.DrawArc(box, redStart, GaugeGeometry.AngleAt(1), true, false);
        }

        // 3. Ticks. Cheap, and most of what makes it read as an instrument
        //    rather than a progress bar.
        DrawTicks(canvas, cx, cy, r, stroke);

        // 4. Glow — the same arc, fatter and faint. Two lines, disproportionate payoff.
        if (t > 0.001)
        {
            canvas.StrokeSize = stroke * 2.2f;
            canvas.StrokeColor = accent.WithAlpha(0.16f);
            canvas.DrawArc(box, GaugeGeometry.StartAngle, endAngle, true, false);

            // 5. Value arc.
            canvas.StrokeSize = stroke;
            canvas.StrokeColor = accent;
            canvas.DrawArc(box, GaugeGeometry.StartAngle, endAngle, true, false);
        }

        // 6. Peak hold — where this gauge has been this session.
        DrawPeak(canvas, cx, cy, r, stroke);

        DrawText(canvas, rect, cx, cy, size, displayed);
    }

    private void DrawTicks(ICanvas canvas, float cx, float cy, float r, float stroke)
    {
        var outer = r - stroke * 1.4f;

        for (var i = 0; i <= 10; i++)
        {
            var major = i % 5 == 0;
            var angle = GaugeGeometry.AngleAt(i / 10.0);
            var inner = outer - (major ? stroke * 1.1f : stroke * 0.6f);

            var (x0, y0) = GaugeGeometry.PointOn(cx, cy, outer, angle);
            var (x1, y1) = GaugeGeometry.PointOn(cx, cy, inner, angle);

            canvas.StrokeSize = major ? 2f : 1f;
            canvas.StrokeColor = major ? MotorcitoPalette.TickMajor : MotorcitoPalette.TickMinor;
            canvas.DrawLine(x0, y0, x1, y1);
        }
    }

    private void DrawPeak(ICanvas canvas, float cx, float cy, float r, float stroke)
    {
        if (owner.Peak is not { } peak || peak <= owner.Min)
            return;

        var angle = GaugeGeometry.AngleAt(GaugeGeometry.Fraction(peak, owner.Min, owner.Max));
        var (x0, y0) = GaugeGeometry.PointOn(cx, cy, r + stroke * 0.75f, angle);
        var (x1, y1) = GaugeGeometry.PointOn(cx, cy, r - stroke * 0.75f, angle);

        canvas.StrokeSize = 2f;
        canvas.StrokeColor = GaugeGeometry.ColorFor(peak, owner.Warn, owner.Redline).WithAlpha(0.75f);
        canvas.DrawLine(x0, y0, x1, y1);
    }

    private void DrawText(ICanvas canvas, RectF rect, float cx, float cy, float size, double displayed)
    {
        // A gauge whose PID dropped out of the last snapshot shows its last
        // reading dimmed rather than snapping to zero — a cheap adapter drops
        // frames constantly and a gauge slamming to 0 looks broken.
        var alpha = owner.IsStale || owner.IsUnsupported ? 0.35f : 1f;

        // Text rects are deliberately much taller than their font size.
        // DrawString defaults to TextFlow.ClipBounds, and a rect only as tall as
        // the nominal font size clips the line's ascender and descender — which
        // silently drops the whole string rather than trimming it.
        canvas.FontColor = MotorcitoPalette.Text.WithAlpha(alpha);
        canvas.Font = GraphicsFont.DefaultBold;
        // 0.21 rather than larger so a four-digit RPM still clears the arc's
        // inner edge at the smaller size used in the hero grid.
        canvas.FontSize = size * 0.21f;
        // Three distinct states, because the gauges are always on screen:
        // a live reading, "not offered by this car", and "nothing yet".
        var numeral = owner.IsUnsupported
            ? "n/a"
            : owner.HasValue ? displayed.ToString($"F{owner.Decimals}") : "—";
        canvas.DrawString(
            numeral,
            cx - size * 0.5f, cy - size * 0.26f, size, size * 0.44f,
            HorizontalAlignment.Center,
            VerticalAlignment.Center);

        canvas.FontColor = MotorcitoPalette.TextDim.WithAlpha(alpha);
        canvas.Font = GraphicsFont.Default;
        canvas.FontSize = size * 0.095f;
        canvas.DrawString(
            owner.Unit,
            cx - size * 0.5f, cy + size * 0.09f, size, size * 0.18f,
            HorizontalAlignment.Center,
            VerticalAlignment.Center);

        canvas.FontColor = MotorcitoPalette.Caption;
        canvas.FontSize = size * 0.085f;
        canvas.DrawString(
            owner.Label.ToUpperInvariant(),
            cx - size * 0.5f, rect.Bottom - size * 0.17f, size, size * 0.17f,
            HorizontalAlignment.Center,
            VerticalAlignment.Center);
    }
}
