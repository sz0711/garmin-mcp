using System.Globalization;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>Renders the report as phone-friendly Markdown (GitHub mobile app).</summary>
public static class MarkdownRenderer
{
    public static string Render(GarminReport report, int showDays = 14)
    {
        var days = report.Days
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .Take(showDays)
            .ToList();
        var chrono = days.AsEnumerable().Reverse().ToList(); // oldest -> newest for trends

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

        sb.AppendLine($"## Trends (letzte {chrono.Count} Tage)");
        sb.AppendLine();
        sb.AppendLine($"- Ruhepuls `{Sparkline.Render(chrono.Select(d => (double?)d.RestingHeartRate))}` {Range(chrono.Select(d => (double?)d.RestingHeartRate), "")}");
        sb.AppendLine($"- HRV `{Sparkline.Render(chrono.Select(d => (double?)d.HrvLastNight))}` {Range(chrono.Select(d => (double?)d.HrvLastNight), "")}");
        sb.AppendLine($"- Schlaf `{Sparkline.Render(chrono.Select(d => d.SleepHours))}` {Range(chrono.Select(d => d.SleepHours), "h")}");
        sb.AppendLine();

        AppendMermaidCharts(sb, report);

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

    private static void AppendMermaidCharts(StringBuilder sb, GarminReport report)
    {
        var days = report.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        if (days.Count > 14) days = days.Skip(days.Count - 14).ToList();

        var charts = new StringBuilder();
        void Chart(string title, string axis, Func<DayMetrics, double?> selector)
        {
            var pts = days
                .Where(d => selector(d).HasValue)
                .Select(d => (label: d.Date.Length >= 10 ? d.Date[5..] : d.Date, val: selector(d)!.Value))
                .ToList();
            if (pts.Count < 2) return;
            charts.AppendLine("```mermaid");
            charts.AppendLine("xychart-beta");
            charts.AppendLine($"  title \"{title}\"");
            charts.AppendLine("  x-axis [" + string.Join(", ", pts.Select(p => $"\"{p.label}\"")) + "]");
            charts.AppendLine($"  y-axis \"{axis}\"");
            charts.AppendLine("  line [" + string.Join(", ", pts.Select(p => p.val.ToString("0.#", CultureInfo.InvariantCulture))) + "]");
            charts.AppendLine("```");
            charts.AppendLine();
        }

        Chart("Ruhepuls (bpm)", "bpm", d => (double?)d.RestingHeartRate);
        Chart("HRV (ms)", "ms", d => (double?)d.HrvLastNight);
        Chart("Schlaf (h)", "h", d => d.SleepHours);
        Chart("Body Battery (Peak)", "BB", d => (double?)d.BodyBatteryHigh);
        Chart("Stress (Ø)", "Level", d => (double?)d.StressAvg);
        Chart("VO2max", "ml/kg/min", d => d.Vo2Max);
        Chart("ACWR (Trainingslast)", "ratio", d => d.Acwr);

        if (charts.Length == 0) return;
        sb.AppendLine("## Charts");
        sb.AppendLine();
        sb.Append(charts);
    }

    private static string DescribePlan(PlannedWorkout p) =>
        $"{p.Title ?? p.Type.ToString()}{(p.DistanceKm is double km ? $", {km} km" : p.DurationMin is double m ? $", {m:0} min" : "")}";

    internal static string FormatTime(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    private static string Val(int? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Val(double? v, string unit) => v is null ? "–" : $"{v}{(unit.Length > 0 ? " " + unit : "")}";
    private static string Cell(int? v) => v?.ToString() ?? "–";
    private static string Cell(double? v) => v?.ToString() ?? "–";

    private static string Range(IEnumerable<double?> values, string unit)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (present.Count == 0) return "";
        var min = present.Min();
        var max = present.Max();
        var u = unit.Length > 0 ? " " + unit : "";
        return min == max ? $"({min}{u})" : $"({min}–{max}{u})";
    }
}
