using System.Globalization;

namespace GarminMcp.Core.Reporting;

/// <summary>Aggregated training for a calendar week (Mon–Sun).</summary>
public sealed class WeeklyStats
{
    public double Km { get; set; }
    public double Hours { get; set; }
    public int Sessions { get; set; }
    public double LongestKm { get; set; }
    public double ElevationM { get; set; }
    public int IntensityMinutes { get; set; }
    public Dictionary<string, double> KmByType { get; } = new();
}

/// <summary>Builds this-week and last-week training summaries from activities + daily metrics.</summary>
public static class TrainingWeek
{
    public static (WeeklyStats Current, WeeklyStats Previous) Summarize(
        IReadOnlyList<ActivitySummary> activities, IReadOnlyList<DayMetrics> days, DateOnly today)
    {
        var offset = ((int)today.DayOfWeek + 6) % 7; // days since Monday
        var curStart = today.AddDays(-offset);
        var prevStart = curStart.AddDays(-7);

        return (
            Build(activities, days, curStart, curStart.AddDays(6)),
            Build(activities, days, prevStart, curStart.AddDays(-1)));
    }

    private static WeeklyStats Build(
        IReadOnlyList<ActivitySummary> activities, IReadOnlyList<DayMetrics> days, DateOnly start, DateOnly end)
    {
        var stats = new WeeklyStats();

        foreach (var a in activities)
        {
            if (!DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                continue;
            if (d < start || d > end) continue;

            stats.Sessions++;
            var km = a.DistanceKm ?? 0;
            stats.Km += km;
            stats.Hours += (a.DurationMin ?? 0) / 60.0;
            stats.ElevationM += a.ElevationGainM ?? 0;
            if (km > stats.LongestKm) stats.LongestKm = km;
            var type = string.IsNullOrWhiteSpace(a.Type) ? "andere" : a.Type!;
            stats.KmByType[type] = stats.KmByType.GetValueOrDefault(type) + km;
        }

        foreach (var day in days)
        {
            if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                continue;
            if (d < start || d > end) continue;
            stats.IntensityMinutes += day.IntensityMinutes;
        }

        stats.Km = Math.Round(stats.Km, 1);
        stats.Hours = Math.Round(stats.Hours, 1);
        stats.LongestKm = Math.Round(stats.LongestKm, 1);
        stats.ElevationM = Math.Round(stats.ElevationM, 0);
        return stats;
    }
}
