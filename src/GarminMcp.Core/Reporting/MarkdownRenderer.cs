using System.Globalization;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>Renders the report as a phone-friendly Markdown dashboard (GitHub mobile app).</summary>
public static class MarkdownRenderer
{
    public static string Render(GarminReport report, int showDays = 14)
    {
        var today = report.Coaching is { } cc && DateOnly.TryParse(cc.Date, out var d0)
            ? d0
            : report.Days.Count > 0 && DateOnly.TryParse(report.Days.Max(x => x.Date), out var d1)
                ? d1
                : DateOnly.FromDateTime(report.GeneratedAtUtc.UtcDateTime);

        var days = report.Days
            .OrderByDescending(x => x.Date, StringComparer.Ordinal)
            .Take(showDays)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 🏃 Garmin Dashboard");
        sb.AppendLine();
        sb.AppendLine($"_Aktualisiert: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine();

        AppendKpiBar(sb, report, days, today);
        AppendCoaching(sb, report);
        AppendLatestDay(sb, days);
        AppendWeek(sb, report, today);
        AppendCharts(sb, report, today);
        AppendDetails(sb, report, days);

        return sb.ToString();
    }

    // ---- Glance bar ----------------------------------------------------------
    private static void AppendKpiBar(StringBuilder sb, GarminReport report, List<DayMetrics> days, DateOnly today)
    {
        var latest = days.FirstOrDefault(d => d.HasAnyData);
        if (latest is null && report.Coaching is null) return;

        var prior = days.Where(d => string.CompareOrdinal(d.Date, latest?.Date ?? "") < 0).ToList();
        var parts = new List<string>();

        if (report.Coaching is { } c)
        {
            var dot = c.Readiness switch { Readiness.Green => "🟢", Readiness.Amber => "🟡", _ => "🔴" };
            parts.Add($"{dot} **Readiness {(c.ReadinessScore?.ToString() ?? c.Readiness.ToString())}**");
        }
        if (latest?.RestingHeartRate is int rhr)
            parts.Add($"❤️ {rhr} bpm{Arrow(rhr, Avg(prior, x => x.RestingHeartRate))}");
        if (latest?.HrvLastNight is int hrv)
            parts.Add($"💓 {hrv} ms{Arrow(hrv, Avg(prior, x => x.HrvLastNight))}");
        if (latest?.SleepHours is double sl)
            parts.Add($"😴 {sl:0.0} h");
        if (latest?.BodyBatteryHigh is int bb)
            parts.Add($"🔋 {bb}");

        var (cur, _) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.Km > 0) parts.Add($"🏃 {cur.Km:0.#} km/Wo");
        if (report.Coaching?.Tsb is double tsb) parts.Add($"🎚️ Form {tsb:+0;-0;0}");
        var streak = Streak(report.Activities, today);
        if (streak > 0) parts.Add($"🔥 {streak} {(streak == 1 ? "Tag" : "Tage")} Streak");
        if (report.Coaching?.DaysToRace is int dtr) parts.Add($"🏁 {dtr} T");

        if (parts.Count == 0) return;
        sb.AppendLine("> " + string.Join("  ·  ", parts));
        sb.AppendLine();
    }

    private static void AppendLatestDay(StringBuilder sb, List<DayMetrics> days)
    {
        var latest = days.FirstOrDefault(d => d.HasAnyData);
        if (latest is null) return;

        sb.AppendLine($"## Letzter Tag — {latest.Date}");
        sb.AppendLine();
        sb.AppendLine($"- ❤️ Ruhepuls: {Val(latest.RestingHeartRate, "bpm")}");
        sb.AppendLine($"- 💓 HRV (letzte Nacht): {Val(latest.HrvLastNight, "ms")}{(latest.HrvStatus is null ? "" : $" ({latest.HrvStatus})")}");
        sb.AppendLine($"- 😴 Schlaf: {Val(latest.SleepHours, "h")}");
        if (latest.SleepDeepMin is not null || latest.SleepLightMin is not null || latest.SleepRemMin is not null)
            sb.AppendLine($"  - 🛌 Phasen: Tief {Dur(latest.SleepDeepMin)} · Leicht {Dur(latest.SleepLightMin)} · REM {Dur(latest.SleepRemMin)} · Wach {Dur(latest.SleepAwakeMin)}");
        sb.AppendLine($"- 🔋 Body Battery: {(latest.BodyBatteryLow is null && latest.BodyBatteryHigh is null ? "–" : $"{latest.BodyBatteryLow?.ToString() ?? "?"} → {latest.BodyBatteryHigh?.ToString() ?? "?"}")}");
        sb.AppendLine($"- 😰 Stress (Ø): {Val(latest.StressAvg, "")}");
        sb.AppendLine($"- 👟 Schritte: {Val(latest.Steps, "")}");
        sb.AppendLine($"- 🔥 Kalorien: {Val(latest.Calories, "kcal")}");
        sb.AppendLine($"- ⚡ Intensitätsminuten: {latest.IntensityMinutes}");
        sb.AppendLine();
    }

    // ---- Weekly overview -----------------------------------------------------
    private static void AppendWeek(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var (cur, prev) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.Sessions == 0 && prev.Sessions == 0) return;

        sb.AppendLine("## 🗓️ Wochenüberblick (Mo–So)");
        sb.AppendLine();
        sb.AppendLine("| Metrik | Diese Woche | Letzte Woche |");
        sb.AppendLine("|---|--:|--:|");
        sb.AppendLine($"| 🏃 Distanz | **{cur.Km:0.#} km**{Arrow(cur.Km, prev.Km)} {PctParen(cur.Km, prev.Km)} | {prev.Km:0.#} km |");
        sb.AppendLine($"| ⏱️ Zeit | {cur.Hours:0.#} h{Arrow(cur.Hours, prev.Hours)} | {prev.Hours:0.#} h |");
        sb.AppendLine($"| 🔁 Einheiten | {cur.Sessions}{Arrow(cur.Sessions, prev.Sessions)} | {prev.Sessions} |");
        sb.AppendLine($"| 📏 Längster Lauf | {cur.LongestKm:0.#} km{Arrow(cur.LongestKm, prev.LongestKm)} | {prev.LongestKm:0.#} km |");
        sb.AppendLine($"| ⛰️ Höhenmeter | {cur.ElevationM:0} m{Arrow(cur.ElevationM, prev.ElevationM)} | {prev.ElevationM:0} m |");
        sb.AppendLine($"| ⚡ Intensitätsminuten | {cur.IntensityMinutes}{Arrow(cur.IntensityMinutes, prev.IntensityMinutes)} | {prev.IntensityMinutes} |");
        sb.AppendLine();

        if (cur.KmByType.Count > 0)
        {
            var split = string.Join(" · ", cur.KmByType
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value:0.#} km"));
            sb.AppendLine($"Aufteilung: {split}");
            sb.AppendLine();
        }
    }

    // ---- Charts --------------------------------------------------------------
    private static void AppendCharts(StringBuilder sb, GarminReport report, DateOnly today)
    {
        var days = report.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        if (days.Count > 21) days = days.Skip(days.Count - 21).ToList();
        var labels = days.Select(d => Short(d.Date)).ToList();

        var charts = new StringBuilder();

        // Form / Performance-Management-Chart
        var (pmc, _, _, _) = LoadModel.Compute(report.Activities, today, 28);
        if (pmc.Count >= 2)
        {
            var pl = pmc.Select(p => Short(p.Date)).ToList();
            Multi(charts, "🏋️ Form: Fitness vs. Fatigue", "Load", new[] { "#0a84ff", "#ff9f0a" }, pl,
                new (string, IReadOnlyList<double?>)[]
                {
                    ("Fitness", pmc.Select(p => (double?)p.Ctl).ToList()),
                    ("Fatigue", pmc.Select(p => (double?)p.Atl).ToList()),
                });
            Single(charts, "🎚️ Form (TSB = Fitness − Fatigue)", "TSB", "#ffd60a", "line", pl, pmc.Select(p => (double?)p.Tsb).ToList());
        }

        Single(charts, "🎯 Readiness", "Score", "#5856d6", "line", labels, days.Select(d => d.ReadinessScore is int r ? (double?)r : null).ToList());

        var rhr = days.Select(d => d.RestingHeartRate is int v ? (double?)v : null).ToList();
        Multi(charts, "❤️ Ruhepuls + Ø7T (bpm)", "bpm", new[] { "#ff5a5f", "#ffc2c4" }, labels,
            new (string, IReadOnlyList<double?>)[] { ("Wert", rhr), ("Ø7T", Rolling7(rhr)) });

        var hrv = days.Select(d => d.HrvLastNight is int v ? (double?)v : null).ToList();
        Multi(charts, "💓 HRV + Ø7T (ms)", "ms", new[] { "#0a84ff", "#a9d2ff" }, labels,
            new (string, IReadOnlyList<double?>)[] { ("Wert", hrv), ("Ø7T", Rolling7(hrv)) });

        Single(charts, "😴 Schlaf (h)", "h", "#30d158", "bar", labels, days.Select(d => d.SleepHours).ToList());
        Single(charts, "👟 Schritte", "Schritte", "#5e5ce6", "bar", labels, days.Select(d => d.Steps is int v ? (double?)v : null).ToList());
        Single(charts, "🔋 Body Battery (Peak)", "BB", "#34c759", "line", labels, days.Select(d => d.BodyBatteryHigh is int v ? (double?)v : null).ToList());
        Single(charts, "😰 Stress (Ø)", "Level", "#ff375f", "bar", labels, days.Select(d => d.StressAvg is int v ? (double?)v : null).ToList());
        Single(charts, "🫁 VO₂max", "ml/kg/min", "#bf5af2", "line", labels, days.Select(d => d.Vo2Max).ToList());
        Single(charts, "📊 ACWR (Trainingslast, Ziel 0,8–1,3)", "ratio", "#5e9eff", "line", labels, days.Select(d => d.Acwr).ToList());
        Single(charts, "🛏️ Zubettgeh-Zeit (Std.)", "Uhr", "#5ac8fa", "line", labels, days.Select(d => d.BedtimeHour).ToList());

        var (weekLabels, weekValues) = WeeklyKm(report.Activities);
        Single(charts, "🏃 Wochenkilometer (km)", "km", "#ff9f0a", "bar", weekLabels, weekValues);

        // Pie: this week's volume split by sport
        var (cur, _) = TrainingWeek.Summarize(report.Activities, report.Days, today);
        if (cur.KmByType.Count > 0)
        {
            charts.AppendLine("```mermaid");
            charts.AppendLine("pie showData title Wochenkilometer nach Sportart");
            foreach (var kv in cur.KmByType.OrderByDescending(kv => kv.Value))
                charts.AppendLine($"  \"{kv.Key}\" : {kv.Value.ToString("0.#", CultureInfo.InvariantCulture)}");
            charts.AppendLine("```");
            charts.AppendLine();
        }

        if (charts.Length == 0) return;
        sb.AppendLine("## 📈 Entwicklung");
        sb.AppendLine();
        sb.Append(charts);
    }

    private static void Single(StringBuilder charts, string title, string axis, string color, string type,
        IReadOnlyList<string> labels, IReadOnlyList<double?> values)
    {
        var idx = Enumerable.Range(0, labels.Count).Where(i => values[i].HasValue).ToList();
        if (idx.Count < 2) return;
        ChartHeader(charts, title, axis, new[] { color }, idx, labels);
        charts.AppendLine($"  {type} [" + string.Join(", ", idx.Select(i => values[i]!.Value.ToString("0.#", CultureInfo.InvariantCulture))) + "]");
        charts.AppendLine("```");
        charts.AppendLine();
    }

    private static void Multi(StringBuilder charts, string title, string axis, IReadOnlyList<string> colors,
        IReadOnlyList<string> labels, IReadOnlyList<(string Name, IReadOnlyList<double?> Values)> series)
    {
        var idx = Enumerable.Range(0, labels.Count).Where(i => series[0].Values[i].HasValue).ToList();
        if (idx.Count < 2) return;
        ChartHeader(charts, title, axis, colors, idx, labels);
        foreach (var s in series)
            charts.AppendLine("  line [" + string.Join(", ", idx.Select(i => (s.Values[i] ?? series[0].Values[i] ?? 0).ToString("0.#", CultureInfo.InvariantCulture))) + "]");
        charts.AppendLine("```");
        charts.AppendLine();
    }

    private static void ChartHeader(StringBuilder charts, string title, string axis, IReadOnlyList<string> colors,
        List<int> idx, IReadOnlyList<string> labels)
    {
        charts.AppendLine("```mermaid");
        charts.AppendLine("%%{init: {\"themeVariables\": {\"xyChart\": {\"plotColorPalette\": \"" + string.Join(",", colors) + "\"}}}}%%");
        charts.AppendLine("xychart-beta");
        charts.AppendLine($"  title \"{title}\"");
        charts.AppendLine("  x-axis [" + string.Join(", ", idx.Select(i => $"\"{labels[i]}\"")) + "]");
        charts.AppendLine($"  y-axis \"{axis}\"");
    }

    private static List<double?> Rolling7(IReadOnlyList<double?> values)
    {
        var result = new List<double?>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var window = new List<double>();
            for (var j = Math.Max(0, i - 6); j <= i; j++)
                if (values[j] is double v) window.Add(v);
            result.Add(window.Count > 0 ? Math.Round(window.Average(), 1) : null);
        }
        return result;
    }

    private static (List<string> Labels, List<double?> Values) WeeklyKm(IReadOnlyList<ActivitySummary> activities)
    {
        var byWeek = new Dictionary<int, double>();
        foreach (var a in activities)
        {
            if (a.DistanceKm is not double km || km <= 0) continue;
            if (!DateOnly.TryParseExact(a.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            var dt = d.ToDateTime(TimeOnly.MinValue);
            var key = ISOWeek.GetYear(dt) * 100 + ISOWeek.GetWeekOfYear(dt);
            byWeek[key] = byWeek.GetValueOrDefault(key) + km;
        }
        var ordered = byWeek.OrderBy(kv => kv.Key).ToList();
        if (ordered.Count > 12) ordered = ordered.Skip(ordered.Count - 12).ToList();
        return (
            ordered.Select(kv => "KW" + (kv.Key % 100).ToString("00")).ToList(),
            ordered.Select(kv => (double?)Math.Round(kv.Value, 1)).ToList());
    }

    // ---- Collapsible detail --------------------------------------------------
    private static void AppendDetails(StringBuilder sb, GarminReport report, List<DayMetrics> days)
    {
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>📋 Verlauf (Tabelle)</summary>");
        sb.AppendLine();
        sb.AppendLine("| Datum | Ruhe-HR | HRV | Schlaf | Schritte | Stress | Body Battery | kcal |");
        sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|");
        foreach (var d in days)
        {
            var bb = d.BodyBatteryLow is null && d.BodyBatteryHigh is null
                ? "–"
                : $"{d.BodyBatteryLow?.ToString() ?? "?"}→{d.BodyBatteryHigh?.ToString() ?? "?"}";
            sb.AppendLine($"| {d.Date} | {Cell(d.RestingHeartRate)} | {Cell(d.HrvLastNight)} | {Cell(d.SleepHours)} | {Cell(d.Steps)} | {Cell(d.StressAvg)} | {bb} | {Cell(d.Calories)} |");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        var activities = report.Activities
            .OrderByDescending(a => a.Date, StringComparer.Ordinal)
            .ThenByDescending(a => a.Id)
            .Take(12)
            .ToList();
        if (activities.Count == 0) return;

        sb.AppendLine("<details>");
        sb.AppendLine("<summary>🏃 Letzte Aktivitäten</summary>");
        sb.AppendLine();
        foreach (var a in activities)
        {
            var parts = new List<string>();
            if (a.DistanceKm is not null) parts.Add($"{a.DistanceKm} km");
            if (a.DurationMin is not null) parts.Add($"{a.DurationMin} min");
            if (a.ElevationGainM is not null) parts.Add($"{a.ElevationGainM:0} hm");
            if (a.Calories is not null) parts.Add($"{a.Calories} kcal");
            if (a.AverageHr is not null) parts.Add($"ø {a.AverageHr} bpm");
            var detail = parts.Count > 0 ? " — " + string.Join(", ", parts) : "";
            sb.AppendLine($"- **{a.Date}** {a.Name ?? a.Type ?? "Aktivität"} _({a.Type ?? "?"})_{detail}");
        }
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    // ---- Coaching block ------------------------------------------------------
    private static void AppendCoaching(StringBuilder sb, GarminReport report)
    {
        var c = report.Coaching;
        if (c is null) return;

        sb.AppendLine($"## 🧠 Coach — {c.Date}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(report.CoachInsight))
        {
            sb.AppendLine(report.CoachInsight!.Trim());
            sb.AppendLine();
        }

        var score = c.ReadinessScore is int sc
            ? $"  ·  Readiness {sc}{(c.ReadinessLevel is null ? "" : $" ({c.ReadinessLevel})")}"
            : "";
        sb.AppendLine($"**{c.Headline}**{score}");
        sb.AppendLine();

        foreach (var r in c.Rationale)
            sb.AppendLine($"- {r}");

        if (c.PlanToday.Count > 0)
            sb.AppendLine($"- 📋 Plan heute: {string.Join(", ", c.PlanToday.Select(p => p.Title ?? p.Type.ToString()))}{(c.PlanNote is null ? "" : $" — {c.PlanNote}")}");
        else if (c.PlanNote is not null)
            sb.AppendLine($"- 📋 {c.PlanNote}");

        if (c.NextLongRun is not null)
            sb.AppendLine($"- 🛣️ Nächster Longrun: {c.NextLongRun.Date} ({DescribePlan(c.NextLongRun)})");
        if (c.NextQuality is not null)
            sb.AppendLine($"- ⚡ Nächste harte Einheit: {c.NextQuality.Date} ({DescribePlan(c.NextQuality)})");

        var statusBits = new List<string>();
        if (c.TrainingStatus is not null) statusBits.Add($"Status: {c.TrainingStatus}");
        if (c.Acwr is double acwr) statusBits.Add($"ACWR {acwr:0.0}");
        if (c.Vo2Max is double v) statusBits.Add($"VO₂max {v:0.0}");
        if (statusBits.Count > 0) sb.AppendLine($"- 📈 {string.Join(" · ", statusBits)}");

        var formBits = new List<string>();
        if (c.Ctl is double ctl) formBits.Add($"Fitness {ctl:0}");
        if (c.Atl is double atl) formBits.Add($"Fatigue {atl:0}");
        if (c.Tsb is double tsb) formBits.Add($"Form {tsb:+0;-0;0} ({FormLabel(tsb)})");
        if (formBits.Count > 0) sb.AppendLine($"- 🏋️ {string.Join(" · ", formBits)}");

        if (c.PlannedThisWeek is int planned && planned > 0)
            sb.AppendLine($"- ✅ Planerfüllung diese Woche: {c.DoneThisWeek ?? 0}/{planned}");
        if (c.SleepConsistencyMin is double scm)
            sb.AppendLine($"- 🛏️ Schlaf-Konsistenz: ±{scm:0} min (Zubettgeh-Zeit)");

        if (c.RaceDate is not null)
        {
            var racePart = $"🏁 Rennen: {c.RaceDate}" + (c.DaysToRace is int d ? $" (in {d} Tagen)" : "");
            if (c.Race?.MarathonSeconds is int ms) racePart += $" · Marathon-Prognose {FormatTime(ms)}";
            if (!string.IsNullOrWhiteSpace(c.Goal)) racePart += $" · Ziel {c.Goal}";
            sb.AppendLine($"- {racePart}");
        }
        else if (c.Race?.MarathonSeconds is int ms2)
        {
            sb.AppendLine($"- 🏁 Marathon-Prognose {FormatTime(ms2)}{(string.IsNullOrWhiteSpace(c.Goal) ? "" : $" · Ziel {c.Goal}")}");
        }

        if (c.TaperNote is not null) sb.AppendLine($"- ⏳ {c.TaperNote}");

        if (c.Nutrition is { } n)
        {
            sb.AppendLine($"- 🍽️ Ernährung heute ({n.DayType}): ~{n.CalorieTarget} kcal — KH {n.CarbsG} g · Eiweiß {n.ProteinG} g · Fett {n.FatG} g{(n.WeightKg is double w ? $" (bei {w} kg)" : "")}");
            sb.AppendLine($"  - {n.Guidance}");
        }

        sb.AppendLine();
    }

    // ---- Helpers -------------------------------------------------------------
    private static string DescribePlan(PlannedWorkout p) =>
        $"{p.Title ?? p.Type.ToString()}{(p.DistanceKm is double km ? $", {km} km" : p.DurationMin is double m ? $", {m:0} min" : "")}";

    internal static string FormatTime(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string Arrow(double cur, double? prev)
    {
        if (prev is not double p || p == 0) return "";
        if (cur > p * 1.02) return " ▲";
        if (cur < p * 0.98) return " ▼";
        return "";
    }

    private static string PctParen(double cur, double prev)
    {
        if (prev <= 0) return "";
        var pct = (cur - prev) / prev * 100;
        return $"({pct:+0;-0;0}%)";
    }

    private static double? Avg(IEnumerable<DayMetrics> days, Func<DayMetrics, int?> selector)
    {
        var v = days.Select(selector).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
    }

    private static int Streak(IReadOnlyList<ActivitySummary> activities, DateOnly today)
    {
        var set = activities.Select(a => a.Date).ToHashSet();
        var d = today;
        if (!set.Contains(d.ToString("yyyy-MM-dd"))) d = d.AddDays(-1); // today may be unlogged yet
        var n = 0;
        while (set.Contains(d.ToString("yyyy-MM-dd"))) { n++; d = d.AddDays(-1); }
        return n;
    }

    private static string FormLabel(double tsb) => tsb switch
    {
        > 15 => "sehr frisch",
        > 5 => "frisch",
        >= -10 => "ausbalanciert",
        >= -20 => "ermüdet",
        _ => "stark ermüdet",
    };

    private static string Short(string isoDate) => isoDate.Length >= 10 ? isoDate[5..] : isoDate;
    private static string Val(int? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Val(double? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Cell(int? v) => v?.ToString() ?? "–";
    private static string Cell(double? v) => v?.ToString() ?? "–";

    private static string Dur(int? minutes)
    {
        if (minutes is not int m) return "–";
        return m >= 60 ? $"{m / 60}:{m % 60:00} h" : $"{m} min";
    }
}
