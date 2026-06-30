using Garmin.Connect.Models;
using GarminMcp.Core.Coaching;
using GarminMcp.Core.Metrics;

namespace GarminMcp.Core.Reporting;

/// <summary>
/// Builds a <see cref="GarminReport"/> for the last N days from an
/// <see cref="IGarminService"/>. Resilient: a failure for one day/metric leaves that
/// value null rather than aborting the whole report.
/// </summary>
public static class ReportBuilder
{
    public static async Task<GarminReport> BuildAsync(
        IGarminService service, int days, DateOnly today, DateTimeOffset generatedAtUtc,
        GarminMetricsClient? metrics = null, string? goal = null,
        CancellationToken cancellationToken = default)
    {
        if (days < 1) days = 1;
        var start = today.AddDays(-(days - 1));
        var report = new GarminReport { GeneratedAtUtc = generatedAtUtc };

        // HRV comes as a range; index by day.
        var hrvByDate = new Dictionary<string, GarminHrvSummary>(StringComparer.Ordinal);
        try
        {
            var hrv = await service.GetHrvAsync(Iso(start), Iso(today), cancellationToken);
            foreach (var s in hrv.HrvSummaries ?? Array.Empty<GarminHrvSummary>())
                hrvByDate[s.CalendarDate.ToString("yyyy-MM-dd")] = s;
        }
        catch
        {
            // HRV unavailable — leave it out.
        }

        for (var i = 0; i < days; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var day = start.AddDays(i);
            var key = Iso(day);
            var m = new DayMetrics { Date = key };

            try
            {
                var st = await service.GetDailySummaryAsync(key, cancellationToken);
                m.RestingHeartRate = Pos(st.RestingHeartRate);
                m.Steps = Pos(st.TotalSteps);
                m.StressAvg = Pos(st.AverageStressLevel);
                m.BodyBatteryHigh = Pos(st.BodyBatteryHighestValue);
                m.BodyBatteryLow = Pos(st.BodyBatteryLowestValue);
                m.Calories = Pos((long)Math.Round(st.TotalKilocalories));
                m.IntensityMinutes = (int)(st.ModerateIntensityMinutes + st.VigorousIntensityMinutes);
            }
            catch
            {
                // no daily summary for this day
            }

            try
            {
                var sleep = await service.GetSleepAsync(key, cancellationToken);
                var seconds = sleep.DailySleepDto?.SleepTimeSeconds ?? 0;
                if (seconds > 0)
                    m.SleepHours = Math.Round(seconds / 3600.0, 1);
            }
            catch
            {
                // no sleep for this day
            }

            if (hrvByDate.TryGetValue(key, out var h))
            {
                m.HrvLastNight = h.LastNightAvg > 0 ? h.LastNightAvg : null;
                m.HrvStatus = string.IsNullOrWhiteSpace(h.Status) ? null : h.Status;
            }

            report.Days.Add(m);
        }

        try
        {
            var activities = await service.GetActivitiesByDateAsync(Iso(start), Iso(today), null, cancellationToken);
            report.Activities = activities.Select(a => new ActivitySummary
            {
                Id = a.ActivityId,
                Date = a.StartTimeLocal.ToString("yyyy-MM-dd"),
                Name = a.ActivityName,
                Type = a.ActivityType?.TypeKey,
                DistanceKm = a.Distance > 0 ? Math.Round(a.Distance / 1000.0, 2) : null,
                DurationMin = a.Duration > 0 ? Math.Round(a.Duration / 60.0, 1) : null,
                Calories = Pos((long)Math.Round(a.Calories)),
                AverageHr = Pos((long)Math.Round(a.AverageHr)),
            }).ToList();
        }
        catch
        {
            // no activities in range
        }

        // --- Coaching (best-effort; never aborts the report) ---
        try
        {
            TrainingReadiness? readiness = metrics is null ? null : await metrics.GetTrainingReadinessAsync(today, cancellationToken);
            TrainingStatusInfo? status = metrics is null ? null : await metrics.GetTrainingStatusAsync(today, cancellationToken);

            string? displayName = null;
            try { displayName = (await service.GetProfileAsync(cancellationToken)).DisplayName; }
            catch { /* profile unavailable */ }

            RacePrediction? race = null;
            if (metrics is not null && !string.IsNullOrWhiteSpace(displayName))
                race = await metrics.GetRacePredictionsAsync(displayName!, cancellationToken);

            var plan = await TrainingPlanReader.BuildAsync(service, today, cancellationToken);
            report.Coaching = CoachEngine.Evaluate(today, report.Days, readiness, status, plan, race, goal);
        }
        catch
        {
            // coaching is an enrichment — leave it null on failure
        }

        return report;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static int? Pos(long v) => v > 0 ? (int)v : null;
}
