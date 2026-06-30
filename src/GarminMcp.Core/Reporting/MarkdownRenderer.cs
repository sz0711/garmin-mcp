using System.Text;

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
