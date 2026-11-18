using System.Globalization;

namespace GitBench.Components.Sprints;

/// <summary>Small helpers for the hand-written SVG charts.</summary>
internal static class ChartScale
{
    /// <summary>A "nice" axis maximum (1, 2, 2.5, 5 × 10^n) at or above <paramref name="max"/>, and its tick step.</summary>
    public static (double max, double step) Nice(double max, int ticks = 4)
    {
        if (max <= 0) return (ticks, 1);
        var raw = max / ticks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        if (step < 1) step = 1; // story points and counts are whole numbers
        return (Math.Ceiling(max / step) * step, step);
    }

    /// <summary>Invariant-culture number for SVG attributes.</summary>
    public static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
