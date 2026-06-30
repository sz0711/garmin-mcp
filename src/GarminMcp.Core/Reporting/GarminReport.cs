namespace GarminMcp.Core.Reporting;

/// <summary>Curated wellness metrics for a single day (nulls = no data that day).</summary>
public sealed class DayMetrics
{
    public string Date { get; set; } = "";           // yyyy-MM-dd
    public int? RestingHeartRate { get; set; }
    public int? HrvLastNight { get; set; }
    public string? HrvStatus { get; set; }
    public double? SleepHours { get; set; }
    public int? Steps { get; set; }
    public int? StressAvg { get; set; }
    public int? BodyBatteryHigh { get; set; }
    public int? BodyBatteryLow { get; set; }
    public int? Calories { get; set; }
    public int IntensityMinutes { get; set; }

    public bool HasAnyData =>
        RestingHeartRate is not null || HrvLastNight is not null || SleepHours is not null ||
        Steps is not null || StressAvg is not null || BodyBatteryHigh is not null || Calories is not null;
}

/// <summary>A single workout summary.</summary>
public sealed class ActivitySummary
{
    public long Id { get; set; }
    public string Date { get; set; } = "";           // yyyy-MM-dd (local start)
    public string? Name { get; set; }
    public string? Type { get; set; }
    public double? DistanceKm { get; set; }
    public double? DurationMin { get; set; }
    public int? Calories { get; set; }
    public int? AverageHr { get; set; }
}

/// <summary>The accumulated dashboard data store (persisted as data.json).</summary>
public sealed class GarminReport
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<DayMetrics> Days { get; set; } = new();
    public List<ActivitySummary> Activities { get; set; } = new();

    /// <summary>Merge a freshly fetched window into an existing store (fresh wins per key).</summary>
    public static GarminReport Merge(GarminReport? existing, GarminReport fresh)
    {
        var days = new Dictionary<string, DayMetrics>(StringComparer.Ordinal);
        foreach (var d in existing?.Days ?? Enumerable.Empty<DayMetrics>())
            days[d.Date] = d;
        foreach (var d in fresh.Days)
            days[d.Date] = d;

        var activities = new Dictionary<long, ActivitySummary>();
        foreach (var a in existing?.Activities ?? Enumerable.Empty<ActivitySummary>())
            activities[a.Id] = a;
        foreach (var a in fresh.Activities)
            activities[a.Id] = a;

        return new GarminReport
        {
            GeneratedAtUtc = fresh.GeneratedAtUtc,
            Days = days.Values.OrderByDescending(d => d.Date, StringComparer.Ordinal).ToList(),
            Activities = activities.Values.OrderByDescending(a => a.Date, StringComparer.Ordinal).ThenByDescending(a => a.Id).ToList(),
        };
    }
}
