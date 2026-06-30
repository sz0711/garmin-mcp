using System.Globalization;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>Renders the report as a phone-friendly Markdown dashboard (GitHub mobile app).</summary>
public static class MarkdownRenderer
{
    public static string Render(GarminReport report, int showDays = 14, IReadOnlyList<ChartRef>? charts = null)
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
        AppendChartImages(sb, charts);
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

    // ---- Charts (PNG images; the GitHub mobile app does not render Mermaid) ----------
    private static void AppendChartImages(StringBuilder sb, IReadOnlyList<ChartRef>? charts)
    {
        if (charts is null || charts.Count == 0) return;
        sb.AppendLine("## 📈 Entwicklung");
        sb.AppendLine();
        foreach (var c in charts)
        {
            sb.AppendLine($"**{c.Title}**");
            sb.AppendLine();
            sb.AppendLine($"![{c.Title}]({c.File})");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(c.Caption))
            {
                sb.AppendLine($"> {c.Caption}");
                sb.AppendLine();
            }
        }
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
