using System.Globalization;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>Renders the report as phone-friendly Markdown (GitHub mobile app), with coloured Mermaid charts.</summary>
public static class MarkdownRenderer
{
    public static string Render(GarminReport report, int showDays = 14)
    {
        var days = report.Days
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .Take(showDays)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# 🏃 Garmin Dashboard");
        sb.AppendLine();
        sb.AppendLine($"_Aktualisiert: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC_");
        sb.AppendLine();

        AppendCoaching(sb, report);

        var latest = days.FirstOrDefault(d => d.HasAnyData);
        if (latest is not null)
        {
            sb.AppendLine($"## Letzter Tag — {latest.Date}");
            sb.AppendLine();
            sb.AppendLine($"- ❤️ Ruhepuls: {Val(latest.RestingHeartRate, "bpm")}");
            sb.AppendLine($"- 💓 HRV (letzte Nacht): {Val(latest.HrvLastNight, "ms")}{(latest.HrvStatus is null ? "" : $" ({latest.HrvStatus})")}");
            sb.AppendLine($"- 😴 Schlaf: {Val(latest.SleepHours, "h")}");
            sb.AppendLine($"- 🔋 Body Battery: {(latest.BodyBatteryLow is null && latest.BodyBatteryHigh is null ? "–" : $"{latest.BodyBatteryLow?.ToString() ?? "?"} → {latest.BodyBatteryHigh?.ToString() ?? "?"}")}");
            sb.AppendLine($"- 😰 Stress (Ø): {Val(latest.StressAvg, "")}");
            sb.AppendLine($"- 👟 Schritte: {Val(latest.Steps, "")}");
            sb.AppendLine($"- 🔥 Kalorien: {Val(latest.Calories, "kcal")}");
            sb.AppendLine($"- ⚡ Intensitätsminuten: {latest.IntensityMinutes}");
            sb.AppendLine();
        }

        AppendCharts(sb, report);

        sb.AppendLine("## Verlauf");
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

        var activities = report.Activities
            .OrderByDescending(a => a.Date, StringComparer.Ordinal)
            .ThenByDescending(a => a.Id)
            .Take(10)
            .ToList();
        if (activities.Count > 0)
        {
            sb.AppendLine("## Letzte Aktivitäten");
            sb.AppendLine();
            foreach (var a in activities)
            {
                var parts = new List<string>();
                if (a.DistanceKm is not null) parts.Add($"{a.DistanceKm} km");
                if (a.DurationMin is not null) parts.Add($"{a.DurationMin} min");
                if (a.Calories is not null) parts.Add($"{a.Calories} kcal");
                if (a.AverageHr is not null) parts.Add($"ø {a.AverageHr} bpm");
                var detail = parts.Count > 0 ? " — " + string.Join(", ", parts) : "";
                sb.AppendLine($"- **{a.Date}** {a.Name ?? a.Type ?? "Aktivität"} _({a.Type ?? "?"})_{detail}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendCharts(StringBuilder sb, GarminReport report)
    {
        var days = report.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        if (days.Count > 21) days = days.Skip(days.Count - 21).ToList();
        var labels = days.Select(d => Short(d.Date)).ToList();

        var charts = new StringBuilder();

        void Series(string title, string axis, string color, string type, Func<DayMetrics, double?> selector)
        {
            var values = days.Select(selector).ToList();
            Emit(charts, title, axis, color, type, labels, values);
        }

        Series("❤️ Ruhepuls (bpm)", "bpm", "#ff5a5f", "line", d => (double?)d.RestingHeartRate);
        Series("💓 HRV (ms)", "ms", "#0a84ff", "line", d => (double?)d.HrvLastNight);
        Series("😴 Schlaf (h)", "h", "#30d158", "bar", d => d.SleepHours);
        Series("👟 Schritte", "Schritte", "#5e5ce6", "bar", d => (double?)d.Steps);
        Series("🔋 Body Battery (Peak)", "BB", "#34c759", "line", d => (double?)d.BodyBatteryHigh);
        Series("😰 Stress (Ø)", "Level", "#ff375f", "bar", d => (double?)d.StressAvg);
        Series("🫁 VO₂max", "ml/kg/min", "#bf5af2", "line", d => d.Vo2Max);
        Series("📊 ACWR (Trainingslast, Ziel 0,8–1,3)", "ratio", "#5e9eff", "line", d => d.Acwr);

        var (weekLabels, weekValues) = WeeklyKm(report.Activities);
        Emit(charts, "🏃 Wochenkilometer (km)", "km", "#ff9f0a", "bar", weekLabels, weekValues);

        if (charts.Length == 0) return;
        sb.AppendLine("## 📈 Entwicklung");
        sb.AppendLine();
        sb.Append(charts);
    }

    private static void Emit(StringBuilder charts, string title, string axis, string color, string type,
        IReadOnlyList<string> labels, IReadOnlyList<double?> values)
    {
        var pts = labels.Zip(values, (l, v) => (l, v)).Where(p => p.v.HasValue).ToList();
        if (pts.Count < 2) return;

        charts.AppendLine("```mermaid");
        charts.AppendLine("%%{init: {\"themeVariables\": {\"xyChart\": {\"plotColorPalette\": \"" + color + "\"}}}}%%");
        charts.AppendLine("xychart-beta");
        charts.AppendLine($"  title \"{title}\"");
        charts.AppendLine("  x-axis [" + string.Join(", ", pts.Select(p => $"\"{p.l}\"")) + "]");
        charts.AppendLine($"  y-axis \"{axis}\"");
        charts.AppendLine($"  {type} [" + string.Join(", ", pts.Select(p => p.v!.Value.ToString("0.#", CultureInfo.InvariantCulture))) + "]");
        charts.AppendLine("```");
        charts.AppendLine();
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

    private static string DescribePlan(PlannedWorkout p) =>
        $"{p.Title ?? p.Type.ToString()}{(p.DistanceKm is double km ? $", {km} km" : p.DurationMin is double m ? $", {m:0} min" : "")}";

    internal static string FormatTime(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string Short(string isoDate) => isoDate.Length >= 10 ? isoDate[5..] : isoDate;
    private static string Val(int? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Val(double? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Cell(int? v) => v?.ToString() ?? "–";
    private static string Cell(double? v) => v?.ToString() ?? "–";
}
