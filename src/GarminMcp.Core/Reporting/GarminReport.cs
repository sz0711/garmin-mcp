using GarminMcp.Core.Coaching;

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

    // Accumulated single-point-per-day metrics (only fetched for "today" each run; preserved on merge).
    public double? Vo2Max { get; set; }
    public double? Acwr { get; set; }
    public int? MarathonSeconds { get; set; }

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
    public DailyCoaching? Coaching { get; set; }
    public string? CoachInsight { get; set; }   // optional LLM-written daily note
    public List<DayMetrics> Days { get; set; } = new();
    public List<ActivitySummary> Activities { get; set; } = new();

    /// <summary>Merge a freshly fetched window into an existing store (fresh wins per key).</summary>
    public static GarminReport Merge(GarminReport? existing, GarminReport fresh)
    {
        var days = new Dictionary<string, DayMetrics>(StringComparer.Ordinal);
        foreach (var d in existing?.Days ?? Enumerable.Empty<DayMetrics>())
            days[d.Date] = d;
        foreach (var d in fresh.Days)
        {
            // Carry forward accumulated single-point metrics the fresh window doesn't refetch.
            if (days.TryGetValue(d.Date, out var prev))
            {
                d.Vo2Max ??= prev.Vo2Max;
                d.Acwr ??= prev.Acwr;
                d.MarathonSeconds ??= prev.MarathonSeconds;
            }
            days[d.Date] = d;
        }

        var activities = new Dictionary<long, ActivitySummary>();
        foreach (var a in existing?.Activities ?? Enumerable.Empty<ActivitySummary>())
            activities[a.Id] = a;
        foreach (var a in fresh.Activities)
            activities[a.Id] = a;

        return new GarminReport
        {
            GeneratedAtUtc = fresh.GeneratedAtUtc,
            Coaching = fresh.Coaching,
            CoachInsight = fresh.CoachInsight,
            Days = days.Values.OrderByDescending(d => d.Date, StringComparer.Ordinal).ToList(),
            Activities = activities.Values.OrderByDescending(a => a.Date, StringComparer.Ordinal).ThenByDescending(a => a.Id).ToList(),
        };
    }
}
