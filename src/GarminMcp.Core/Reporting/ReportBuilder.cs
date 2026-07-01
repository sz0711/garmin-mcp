using System.Globalization;
using System.Text.Json;
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
                // Garmin/WHO intensity minutes count vigorous minutes double (75 min vigorous = 150 IM).
                m.IntensityMinutes = (int)(st.ModerateIntensityMinutes + 2 * st.VigorousIntensityMinutes);
                // Pulse-ox (blood oxygen) — object-typed in the library (absent on devices without the sensor).
                m.SpO2Avg = ClampPct(AsInt(st.AverageSpo2));
                m.SpO2Low = ClampPct(AsInt(st.LowestSpo2));
            }
            catch
            {
                // no daily summary for this day
            }

            try
            {
                var sleep = await service.GetSleepAsync(key, cancellationToken);
                var dto = sleep.DailySleepDto;
                var seconds = dto?.SleepTimeSeconds ?? 0;
                if (seconds > 0)
                    m.SleepHours = Math.Round(seconds / 3600.0, 1);
                if (dto is not null)
                {
                    m.SleepScore = dto.SleepScores?.Overall?.Value is { } sv && sv > 0 ? (int)Math.Round(Convert.ToDouble(sv)) : null;
                    m.SleepDeepMin = Minutes(dto.DeepSleepSeconds);
                    m.SleepLightMin = Minutes(dto.LightSleepSeconds);
                    m.SleepRemMin = Minutes(dto.RemSleepSeconds);
                    m.SleepAwakeMin = Minutes(dto.AwakeSleepSeconds);
                    m.SleepRespirationRate = dto.AverageRespirationValue is > 0 and < 60 ? Math.Round(dto.AverageRespirationValue, 1) : null;
                    if (dto.SleepStartTimestampLocal > 0)
                    {
                        var local = DateTimeOffset.FromUnixTimeMilliseconds(dto.SleepStartTimestampLocal).UtcDateTime;
                        m.BedtimeLocal = local.ToString("HH:mm");
                        var hour = local.TimeOfDay.TotalHours;
                        m.BedtimeHour = Math.Round(hour < 12 ? hour + 24 : hour, 2); // shift past-midnight for continuity
                    }
                }
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
            report.Activities = activities.Select(MapActivity).ToList();
        }
        catch
        {
            // The bulk range fetch failed — most likely because ONE activity in the window has a
            // sensor field the library types as a non-nullable double (running dynamics, training
            // effect) but Garmin actually returned JSON null for (common for non-running or
            // manually-logged activities). System.Text.Json deserializes arrays atomically, so one
            // bad activity fails the WHOLE range. Fall back to fetching day by day so a single
            // unparseable day only costs that day, not the entire rolling window.
            var collected = new List<ActivitySummary>();
            for (var i = 0; i < days; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dayKey = Iso(start.AddDays(i));
                try
                {
                    var dayActivities = await service.GetActivitiesByDateAsync(dayKey, dayKey, null, cancellationToken);
                    collected.AddRange(dayActivities.Select(MapActivity));
                }
                catch
                {
                    // this specific day is unrecoverable (still fails per-day) — skip it, keep the rest
                }
            }
            report.Activities = collected;
        }

        try
        {
            // Splits/laps require one extra API call PER activity — fetching them for every run in
            // the window would multiply the report's call volume many times over (a real concern:
            // excessive polling has already caused a real Garmin auth incident this project hit once).
            // Deliberately scoped to just the single most recent qualifying long run (>= 15 km, within
            // the last 10 days) — the run type where pacing strategy actually matters for marathon
            // training — instead of analyzing every activity.
            var recentLongRun = report.Activities
                .Where(a => a.IsRun && a.DistanceKm is >= 15
                            && DateOnly.TryParse(a.Date, out var lrd) && lrd <= today && today.DayNumber - lrd.DayNumber <= 10)
                .OrderByDescending(a => a.Date, StringComparer.Ordinal)
                .ThenByDescending(a => a.Id) // same-day tiebreak, matching GarminReport.Merge's activity ordering
                .FirstOrDefault();
            if (recentLongRun is not null)
            {
                var splits = await service.GetActivitySplitsAsync(recentLongRun.Id, cancellationToken);
                report.RecentLongRunPacing = SplitAnalyzer.Analyze(splits, recentLongRun);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportBuilder] Activity splits fetch failed: {ex.Message}");
            // pacing analysis is optional
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

            // Persist today's single-point metrics into the day record so they accumulate over runs.
            var todayDay = report.Days.FirstOrDefault(d => d.Date == Iso(today));
            if (todayDay is not null)
            {
                todayDay.Vo2Max ??= status?.Vo2Max;
                todayDay.Acwr ??= status?.Acwr;
                todayDay.MarathonSeconds ??= race?.MarathonSeconds;
                todayDay.ReadinessScore ??= readiness?.Score;
            }

            double? weightKg = null;
            try
            {
                var weight = await service.GetWeightAsync(Iso(start), Iso(today), cancellationToken);
                foreach (var s in weight.DailyWeightSummaries ?? Array.Empty<GarminWeightDailyWeightSummary>())
                {
                    var g = s.LatestWeight?.Weight ?? 0;
                    if (g <= 0) continue;
                    var kg = Math.Round(g > 500 ? g / 1000.0 : g, 1);
                    var dayKey = s.SummaryDate.ToString("yyyy-MM-dd");
                    var dd = report.Days.FirstOrDefault(d => d.Date == dayKey);
                    if (dd is not null) dd.WeightKg = kg;
                }
                weightKg = report.Days
                    .Where(d => d.WeightKg.HasValue)
                    .OrderByDescending(d => d.Date, StringComparer.Ordinal)
                    .Select(d => d.WeightKg)
                    .FirstOrDefault();
                if (weightKg is null && weight.TotalAverage?.Weight is double avg && avg > 0)
                    weightKg = Math.Round(avg > 500 ? avg / 1000.0 : avg, 1);
            }
            catch
            {
                // weight unavailable — nutrition falls back to calorie-based macros
            }

            try
            {
                var comp = await service.GetBodyCompositionAsync(Iso(start), Iso(today), cancellationToken);
                foreach (var s in comp.DateWeightList ?? Array.Empty<GarminDateWeight>())
                {
                    var dayKey = s.CalendarDate.ToString("yyyy-MM-dd");
                    var dd = report.Days.FirstOrDefault(d => d.Date == dayKey);
                    if (dd is null) continue;
                    if (s.BodyFat is > 0 and < 70) dd.BodyFatPercent = Math.Round(s.BodyFat, 1); // plausible body-fat range only
                    // MuscleMass is grams in some Garmin accounts, kg in others (same ambiguity the
                    // WeightKg fetch above already guards against) — apply the same magnitude heuristic.
                    if (s.MuscleMass is > 0 and < 200_000)
                    {
                        var mm = s.MuscleMass > 500 ? s.MuscleMass / 1000.0 : s.MuscleMass;
                        if (mm is > 10 and < 80) dd.MuscleMassKg = Math.Round(mm, 1); // plausible human muscle mass
                    }
                    if (s.VisceralFat is > 0 and < 60) dd.VisceralFatRating = Math.Round(s.VisceralFat, 0); // Garmin's rating scale, ~1-59
                }
            }
            catch
            {
                // no smart scale / body composition unavailable — skip
            }

            var plan = await TrainingPlanReader.BuildAsync(service, today, cancellationToken);
            report.Coaching = CoachEngine.Evaluate(today, report.Days, readiness, status, plan, race, goal, weightKg, report.Activities);

            // Early-warning system (multi-day trends across the accumulated history). Reuses the
            // pace zones + taper window CoachEngine already computed above, rather than recomputing.
            report.Alerts = AlertEngine.Evaluate(report.Days, status, today, report.Activities,
                report.Coaching?.DaysToRace, report.Coaching?.Paces);

            if (report.Coaching is { } coaching)
            {
                var (_, ctl, atl, tsb) = LoadModel.Compute(report.Activities, today);
                coaching.Ctl = ctl;
                coaching.Atl = atl;
                coaching.Tsb = tsb;

                var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                var weekEnd = weekStart.AddDays(6);
                coaching.PlannedThisWeek = plan.AllPlanned.Count(p => p.Type != SessionType.Rest && InWeek(p.Date, weekStart, weekEnd));
                coaching.DoneThisWeek = report.Activities.Count(a => InWeek(a.Date, weekStart, weekEnd));
                var plannedKm = plan.AllPlanned.Where(p => p.Type != SessionType.Rest && InWeek(p.Date, weekStart, weekEnd)).Sum(p => p.DistanceKm ?? 0);
                if (plannedKm > 0) coaching.PlannedKmThisWeek = Math.Round(plannedKm, 1);
                // Running-only, matching TrainingWeek.Build and the planned-km side (a plan's DistanceKm
                // is effectively running distance) — a bike/hike day must not make a running plan look
                // fulfilled.
                coaching.DoneKmThisWeek = Math.Round(report.Activities.Where(a => a.IsRun && InWeek(a.Date, weekStart, weekEnd)).Sum(a => a.DistanceKm ?? 0), 1);

                // Last completed week (for the weekly review).
                var lastStart = weekStart.AddDays(-7);
                var lastEnd = weekStart.AddDays(-1);
                coaching.PlannedLastWeek = plan.AllPlanned.Count(p => p.Type != SessionType.Rest && InWeek(p.Date, lastStart, lastEnd));
                coaching.DoneLastWeek = report.Activities.Count(a => InWeek(a.Date, lastStart, lastEnd));
                var plannedKmLast = plan.AllPlanned.Where(p => p.Type != SessionType.Rest && InWeek(p.Date, lastStart, lastEnd)).Sum(p => p.DistanceKm ?? 0);
                if (plannedKmLast > 0) coaching.PlannedKmLastWeek = Math.Round(plannedKmLast, 1);
                coaching.DoneKmLastWeek = Math.Round(report.Activities.Where(a => a.IsRun && InWeek(a.Date, lastStart, lastEnd)).Sum(a => a.DistanceKm ?? 0), 1);

                var bedtimes = report.Days
                    .Where(d => d.BedtimeHour.HasValue)
                    .OrderByDescending(d => d.Date, StringComparer.Ordinal)
                    .Take(14)
                    .Select(d => d.BedtimeHour!.Value)
                    .ToList();
                if (bedtimes.Count >= 3)
                {
                    var mean = bedtimes.Average();
                    var sd = Math.Sqrt(bedtimes.Select(x => (x - mean) * (x - mean)).Average());
                    coaching.SleepConsistencyMin = Math.Round(sd * 60, 0);
                }
            }
        }
        catch (Exception ex)
        {
            // Coaching is a best-effort enrichment — never abort the whole report over it. But
            // losing the ENTIRE coaching section (readiness, alerts, pace zones, nutrition) is a
            // large, user-visible gap, unlike a single missing day/metric elsewhere in this class —
            // so at minimum leave a diagnostic trail (GitHub Actions captures stderr) instead of
            // failing completely silently, which makes a real incident like this impossible to
            // root-cause after the fact.
            Console.Error.WriteLine($"[ReportBuilder] Coaching computation failed, leaving report.Coaching null: {ex}");
            report.CoachingUnavailable = true;
        }

        try
        {
            var prs = await service.GetPersonalRecordsAsync(cancellationToken);
            report.PersonalBests = MapPersonalBests(prs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportBuilder] Personal records fetch failed: {ex.Message}");
            // personal records are optional
        }

        return report;
    }

    // Garmin PR typeId → (label, value-is-a-time, display order). Running records only.
    private static readonly Dictionary<long, (string Label, bool IsTime, int Order)> PrTypes = new()
    {
        [1] = ("1 km", true, 1),
        [2] = ("1 Meile", true, 2),
        [3] = ("5 km", true, 3),
        [4] = ("10 km", true, 4),
        [7] = ("Längster Lauf", false, 7),
    };

    private static List<PersonalBest> MapPersonalBests(IEnumerable<GarminPersonalRecord> records)
    {
        var best = new Dictionary<long, GarminPersonalRecord>();
        foreach (var r in records ?? Enumerable.Empty<GarminPersonalRecord>())
        {
            if (!PrTypes.TryGetValue(r.TypeId, out var meta) || r.Value <= 0) continue;
            if (!best.TryGetValue(r.TypeId, out var ex)) { best[r.TypeId] = r; continue; }
            // For times keep the smallest; for distances the largest.
            if (meta.IsTime ? r.Value < ex.Value : r.Value > ex.Value) best[r.TypeId] = r;
        }

        var list = new List<PersonalBest>();
        foreach (var (typeId, r) in best)
        {
            var meta = PrTypes[typeId];
            var value = meta.IsTime ? FmtTime((int)Math.Round(r.Value)) : $"{Math.Round(r.Value / 1000.0, 1)} km";
            var date = r.PrStartTimeGmt > 0 ? r.PrStartTimeGmtFormatted.ToString("yyyy-MM-dd") : null;
            list.Add(new PersonalBest { Label = meta.Label, Value = value, Date = date, Order = meta.Order });
        }
        return list.OrderBy(p => p.Order).ToList();
    }

    private static string FmtTime(int s) =>
        s >= 3600 ? $"{s / 3600}:{(s % 3600) / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static int? Pos(long v) => v > 0 ? (int)v : null;
    private static int? Minutes(long seconds) => seconds > 0 ? (int)Math.Round(seconds / 60.0) : null;

    // Several library models type sparsely-populated fields (sensor not present on every device) as
    // `object`, which System.Text.Json deserializes into a boxed JsonElement rather than a concrete type.
    private static int? AsInt(object? o) => o switch
    {
        null => null,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.TryGetInt32(out var i) ? i : (int)Math.Round(je.GetDouble()),
        int i => i,
        long l => (int)l,
        double d => (int)Math.Round(d),
        _ => null,
    };

    private static string? AsString(object? o) => o switch
    {
        null => null,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        string s => s,
        _ => null,
    };

    // A blood-oxygen reading below ~80% overnight is virtually always a motion/perfusion sensor
    // artifact rather than a genuine measurement (severe hypoxemia at rest is a medical emergency,
    // not a quiet data point) — narrower than a bare percentage range, consistent with the
    // plausibility narrowing already applied to respiration rate below.
    private static int? ClampPct(int? v) => v is int i && i is >= 80 and <= 100 ? i : null;
    private static double? InRange(double v, double lo, double hi) => v >= lo && v <= hi ? Math.Round(v, 1) : null;

    private static ActivitySummary MapActivity(GarminActivity a)
    {
        var isRun = (a.ActivityType?.TypeKey ?? "").Contains("run", StringComparison.OrdinalIgnoreCase);
        return new ActivitySummary
        {
            Id = a.ActivityId,
            Date = a.StartTimeLocal.ToString("yyyy-MM-dd"),
            Name = a.ActivityName,
            Type = a.ActivityType?.TypeKey,
            DistanceKm = a.Distance > 0 ? Math.Round(a.Distance / 1000.0, 2) : null,
            DurationMin = a.Duration > 0 ? Math.Round(a.Duration / 60.0, 1) : null,
            ElevationGainM = a.ElevationGain > 0 ? Math.Round(a.ElevationGain, 0) : null,
            Calories = Pos((long)Math.Round(a.Calories)),
            AverageHr = Pos((long)Math.Round(a.AverageHr)),
            // Running dynamics: only meaningful (and only populated by Garmin) for run-type activities.
            CadenceSpm = isRun ? InRange(a.AverageRunningCadenceInStepsPerMinute, 120, 220) : null,
            GroundContactTimeMs = isRun ? InRange(a.AvgGroundContactTime, 150, 400) : null,
            VerticalOscillationCm = isRun ? InRange(a.AvgVerticalOscillation, 3, 20) : null,
            StrideLengthCm = isRun ? InRange(a.AvgStrideLength, 50, 250) : null,
            AerobicEffect = InRange(a.AerobicTrainingEffect, 0, 5),
            AnaerobicEffect = InRange(a.AnaerobicTrainingEffect, 0, 5),
            // Stored raw (like ActivitySummary.Type) — MarkdownRenderer.EffectLabelDe translates it for
            // display, mirroring how SportDe() translates the raw sport-type key only at render time.
            EffectLabel = CleanRawLabel(AsString(a.TrainingEffectLabel)),
        };
    }

    private static string? CleanRawLabel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Equals("NONE", StringComparison.OrdinalIgnoreCase) || t.StartsWith("NONE_", StringComparison.OrdinalIgnoreCase)
            ? null : t;
    }

    private static bool InWeek(string date, DateOnly start, DateOnly end) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
        && d >= start && d <= end;
}
