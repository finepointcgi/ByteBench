using System.Globalization;

namespace ByteBench.Services.Roadmap;

public enum RoadmapZoom { Weeks, Months, Quarters }

/// <summary>A labelled column boundary in the timeline header.</summary>
public sealed record RoadmapTick(DateTime Date, string Label);

/// <summary>
/// Maps dates to horizontal pixels for the roadmap. The range covers every date shown plus today, padded and snapped
/// to whole zoom units (weeks start on Monday, months and quarters on their first day).
/// </summary>
public sealed class RoadmapScale
{
    public RoadmapZoom Zoom { get; }
    public DateTime From { get; }
    /// <summary>Exclusive end of the range.</summary>
    public DateTime To { get; }
    public double PxPerDay { get; }

    private RoadmapScale(RoadmapZoom zoom, DateTime from, DateTime to)
    {
        Zoom = zoom;
        From = from;
        To = to;
        PxPerDay = PixelsPerDay(zoom);
    }

    public static double PixelsPerDay(RoadmapZoom zoom) => zoom switch
    {
        RoadmapZoom.Weeks => 28,
        RoadmapZoom.Months => 6,
        _ => 2,
    };

    public static RoadmapScale For(RoadmapZoom zoom, IEnumerable<DateTime> dates, DateTime today)
    {
        today = today.Date;
        var all = dates.Select(d => d.Date).Append(today).ToList();
        var (min, max) = (all.Min(), all.Max());
        // At least a screenful around today, and a unit of padding on both sides.
        var span = zoom switch { RoadmapZoom.Weeks => 42, RoadmapZoom.Months => 180, _ => 540 };
        if ((max - min).TotalDays < span) max = min.AddDays(span);
        var from = Snap(zoom, Step(zoom, Snap(zoom, min), -1));
        var to = Step(zoom, Snap(zoom, max), 2);
        return new RoadmapScale(zoom, from, to);
    }

    /// <summary>Start of the zoom unit containing <paramref name="d"/>.</summary>
    public static DateTime Snap(RoadmapZoom zoom, DateTime d) => zoom switch
    {
        RoadmapZoom.Weeks => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
        RoadmapZoom.Months => new DateTime(d.Year, d.Month, 1, 0, 0, 0, d.Kind),
        _ => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, d.Kind),
    };

    private static DateTime Step(RoadmapZoom zoom, DateTime d, int n) => zoom switch
    {
        RoadmapZoom.Weeks => d.AddDays(7 * n),
        RoadmapZoom.Months => d.AddMonths(n),
        _ => d.AddMonths(3 * n),
    };

    public double Width => (To - From).TotalDays * PxPerDay;

    /// <summary>Pixel offset of the start of day <paramref name="d"/>.</summary>
    public double X(DateTime d) => (d.Date - From).TotalDays * PxPerDay;

    /// <summary>Whole days for a horizontal drag of <paramref name="dx"/> pixels.</summary>
    public int Days(double dx) => (int)Math.Round(dx / PxPerDay, MidpointRounding.AwayFromZero);

    /// <summary>Header columns: weeks ("Nov 17"), months ("Nov 2026") or quarters ("Q4 2026").</summary>
    public IEnumerable<RoadmapTick> Ticks()
    {
        for (var d = From; d < To; d = Step(Zoom, d, 1))
            yield return new RoadmapTick(d, Zoom switch
            {
                RoadmapZoom.Weeks => d.ToString("MMM d", CultureInfo.InvariantCulture),
                RoadmapZoom.Months => d.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                _ => $"Q{(d.Month - 1) / 3 + 1} {d.Year}",
            });
    }
}
