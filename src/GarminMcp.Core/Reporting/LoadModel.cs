using System.Globalization;

namespace GarminMcp.Core.Reporting;

/// <summary>One day of the Performance Management Chart.</summary>
public sealed class LoadPoint
{
    public string Date { get; set; } = "";
    public double Ctl { get; set; } // Fitness (42-day EWMA of load)
    public double Atl { get; set; } // Fatigue (7-day EWMA of load)
    public double Tsb { get; set; } // Form (CTL - ATL)
}

/// <summary>
/// Performance Management Chart: Fitness (CTL), Fatigue (ATL) and Form (TSB) from a daily
/// training-load series. Load is a TRIMP-style proxy (duration × heart-rate intensity).
/// CTL/ATL are exponentially weighted moving averages (time constants 42 / 7 days).
/// </summary>
public static class LoadModel
{
    public static (List<LoadPoint> Series, double Ctl, double Atl, double Tsb) Compute(
        IReadOnlyList<ActivitySummary> activities, DateOnly today, int chartDays = 28)
    {
        var load = new Dictionary<DateOnly, double>();
        DateOnly? earliest = null;
        foreach (var a in activities)
        {
            if (!DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                continue;
            if (d > today) continue;
            load[d] = load.GetValueOrDefault(d) + ActivityLoad(a);
            if (earliest is null || d < earliest) earliest = d;
        }

        if (earliest is null)
            return (new List<LoadPoint>(), 0, 0, 0);

        double ctl = 0, atl = 0;
        var series = new List<LoadPoint>();
        for (var d = earliest.Value; d <= today; d = d.AddDays(1))
        {
            var l = load.GetValueOrDefault(d);
            ctl += (l - ctl) / 42.0;
            atl += (l - atl) / 7.0;
            series.Add(new LoadPoint
            {
                Date = d.ToString("yyyy-MM-dd"),
                Ctl = Math.Round(ctl, 1),
                Atl = Math.Round(atl, 1),
                Tsb = Math.Round(ctl - atl, 1),
            });
        }

        var last = series[^1];
        var chart = series.Count > chartDays ? series.Skip(series.Count - chartDays).ToList() : series;
        return (chart, last.Ctl, last.Atl, last.Tsb);
    }

    /// <summary>TRIMP-style daily load proxy for one activity.</summary>
    private static double ActivityLoad(ActivitySummary a)
    {
        var duration = a.DurationMin ?? (a.DistanceKm is double km ? km * 6 : 0);
        if (duration <= 0) return 0;
        var intensity = a.AverageHr is int hr && hr > 0 ? Math.Clamp(hr / 140.0, 0.6, 2.0) : 1.0;
        return duration * intensity;
    }
}
