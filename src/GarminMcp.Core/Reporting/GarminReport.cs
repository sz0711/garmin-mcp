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

    public int? SleepScore { get; set; }       // Garmin sleep score 0-100
    public int? SleepDeepMin { get; set; }
    public int? SleepLightMin { get; set; }
    public int? SleepRemMin { get; set; }
    public int? SleepAwakeMin { get; set; }
    public double? BedtimeHour { get; set; }   // local bedtime as decimal hour, shifted to ~18–30 for continuity
    public string? BedtimeLocal { get; set; }  // "HH:mm" for display
    public double? WeightKg { get; set; }      // body weight that day (sparse — only on measurement days)
    public double? BodyFatPercent { get; set; } // body fat % from a smart scale (sparse — only on measurement days)
    public double? MuscleMassKg { get; set; }   // skeletal muscle mass from a smart scale (sparse — only on measurement days)
    public double? VisceralFatRating { get; set; } // visceral-fat rating (Garmin scale, roughly 1-59; healthy is single digits) (sparse)

    // Pulse-ox (blood oxygen) and breathing rate — Garmin's overnight "Pulse Ox"/respiration sensors.
    public int? SpO2Avg { get; set; }          // average blood-oxygen saturation (%)
    public int? SpO2Low { get; set; }          // lowest blood-oxygen saturation (%)
    public double? SleepRespirationRate { get; set; } // average breathing rate during sleep (breaths/min)

    // Accumulated single-point-per-day metrics (only fetched for "today" each run; preserved on merge).
    public double? Vo2Max { get; set; }
    public double? Acwr { get; set; }
    public int? MarathonSeconds { get; set; }
    public int? ReadinessScore { get; set; }

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
    public double? ElevationGainM { get; set; }
    public int? Calories { get; set; }
    public int? AverageHr { get; set; }

    /// <summary>Whether Garmin classifies this as a running activity (incl. treadmill/trail/track).
    /// Single source of truth for "is this a run" so weekly running-distance metrics don't silently
    /// include walks, rides or hikes.</summary>
    public bool IsRun => (Type ?? "").Contains("run", StringComparison.OrdinalIgnoreCase);

    // Running dynamics (only populated for running-type activities on a compatible device).
    public double? CadenceSpm { get; set; }            // average run cadence, steps/min
    public double? GroundContactTimeMs { get; set; }   // average ground contact time, ms
    public double? VerticalOscillationCm { get; set; } // average vertical oscillation, cm
    public double? StrideLengthCm { get; set; }        // average stride length, cm
    public double? AerobicEffect { get; set; }         // Garmin Training Effect, aerobic (0.0-5.0)
    public double? AnaerobicEffect { get; set; }       // Garmin Training Effect, anaerobic (0.0-5.0)
    public string? EffectLabel { get; set; }           // Garmin's raw training-effect label (e.g. "TEMPO_TRAINING_EFFECT_LABEL") — MarkdownRenderer.EffectLabelDe translates it for display
}

/// <summary>An all-time personal best from Garmin (running distance/time records).</summary>
public sealed class PersonalBest
{
    public string Label { get; set; } = "";   // e.g. "5 km", "Längster Lauf"
    public string Value { get; set; } = "";    // formatted "21:30" or "32.1 km"
    public string? Date { get; set; }          // yyyy-MM-dd
    public int Order { get; set; }             // display order
}

/// <summary>The accumulated dashboard data store (persisted as data.json).</summary>
public sealed class GarminReport
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DailyCoaching? Coaching { get; set; }
    public string? CoachInsight { get; set; }   // optional LLM-written daily note
    public string? WeeklyInsight { get; set; }  // optional LLM-written weekly review (refreshed weekly)
    public string? WeeklyInsightWeekStart { get; set; } // ISO Monday the weekly insight belongs to
    public List<HealthAlert> Alerts { get; set; } = new();        // early-warning signals
    public List<PersonalBest> PersonalBests { get; set; } = new();
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
            // Coalesce a previously-stored day forward: the rolling window is refetched every
            // run and the Garmin API frequently returns transient empty responses for past days,
            // which would otherwise silently erase accumulated history. Fresh non-null values win;
            // a fresh null never overwrites a stored-good value.
            if (days.TryGetValue(d.Date, out var prev))
            {
                d.RestingHeartRate ??= prev.RestingHeartRate;
                d.HrvLastNight ??= prev.HrvLastNight;
                d.HrvStatus ??= prev.HrvStatus;
                d.SleepHours ??= prev.SleepHours;
                d.SleepScore ??= prev.SleepScore;
                d.SleepDeepMin ??= prev.SleepDeepMin;
                d.SleepLightMin ??= prev.SleepLightMin;
                d.SleepRemMin ??= prev.SleepRemMin;
                d.SleepAwakeMin ??= prev.SleepAwakeMin;
                d.BedtimeHour ??= prev.BedtimeHour;
                d.BedtimeLocal ??= prev.BedtimeLocal;
                d.Steps ??= prev.Steps;
                d.StressAvg ??= prev.StressAvg;
                d.BodyBatteryHigh ??= prev.BodyBatteryHigh;
                d.BodyBatteryLow ??= prev.BodyBatteryLow;
                d.Calories ??= prev.Calories;
                if (d.IntensityMinutes == 0 && prev.IntensityMinutes > 0) d.IntensityMinutes = prev.IntensityMinutes;
                d.Vo2Max ??= prev.Vo2Max;
                d.Acwr ??= prev.Acwr;
                d.MarathonSeconds ??= prev.MarathonSeconds;
                d.ReadinessScore ??= prev.ReadinessScore;
                d.WeightKg ??= prev.WeightKg;   // weight isn't measured daily — keep the last known
                d.BodyFatPercent ??= prev.BodyFatPercent;
                d.MuscleMassKg ??= prev.MuscleMassKg;
                d.VisceralFatRating ??= prev.VisceralFatRating;
                d.SpO2Avg ??= prev.SpO2Avg;
                d.SpO2Low ??= prev.SpO2Low;
                d.SleepRespirationRate ??= prev.SleepRespirationRate;
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
            WeeklyInsight = fresh.WeeklyInsight ?? existing?.WeeklyInsight, // refreshed weekly, kept in between
            WeeklyInsightWeekStart = fresh.WeeklyInsight is not null ? fresh.WeeklyInsightWeekStart : existing?.WeeklyInsightWeekStart,
            Alerts = fresh.Alerts,                 // recomputed each run
            PersonalBests = fresh.PersonalBests.Count > 0 ? fresh.PersonalBests : (existing?.PersonalBests ?? new()),
            Days = days.Values.OrderByDescending(d => d.Date, StringComparer.Ordinal).ToList(),
            Activities = activities.Values.OrderByDescending(a => a.Date, StringComparer.Ordinal).ThenByDescending(a => a.Id).ToList(),
        };
    }
}
