using System.Globalization;
using System.Net;
using System.Text;
using GarminMcp.Core.Coaching;

namespace GarminMcp.Core.Reporting;

/// <summary>
/// Renders the report as a single self-contained HTML file (inline CSS + inline SVG,
/// no external resources) so it can be opened directly from disk on any device.
/// </summary>
public static class HtmlRenderer
{
    public static string Render(GarminReport report, int showDays = 14)
    {
        var days = report.Days
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .Take(showDays)
            .ToList();
        var latest = days.FirstOrDefault(d => d.HasAnyData);

        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="de"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Garmin Dashboard</title>
            <style>
              :root { color-scheme: light dark; --fg:#111; --muted:#777; --card:#f5f5f7; --accent:#0a84ff; --line:#ddd; }
              @media (prefers-color-scheme: dark){ :root { --fg:#eee; --muted:#999; --card:#1c1c1e; --line:#333; } body{background:#000;} }
              body{font-family:system-ui,sans-serif;max-width:48rem;margin:1.5rem auto;padding:0 1rem;color:var(--fg);}
              h1{font-size:1.5rem;margin:0 0 .25rem;} .muted{color:var(--muted);font-size:.85rem;}
              .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(8rem,1fr));gap:.6rem;margin:1rem 0;}
              .tile{background:var(--card);border-radius:.6rem;padding:.7rem .8rem;}
              .tile .k{font-size:.75rem;color:var(--muted);} .tile .v{font-size:1.3rem;font-weight:650;}
              h2{font-size:1.05rem;margin:1.5rem 0 .5rem;}
              table{border-collapse:collapse;width:100%;font-size:.85rem;} th,td{padding:.35rem .4rem;border-bottom:1px solid var(--line);text-align:right;}
              th:first-child,td:first-child{text-align:left;}
              .charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(17rem,1fr));gap:1rem;margin:1rem 0;}
              .chart-card{background:var(--card);border-radius:.6rem;padding:.6rem .7rem;}
              .chart-title{font-size:.85rem;color:var(--muted);margin-bottom:.25rem;}
              .chart-card svg{display:block;width:100%;height:auto;}
              ul{padding-left:1.1rem;} li{margin:.25rem 0;}
            </style></head><body>
            """);

        sb.Append($"<h1>🏃 Garmin Dashboard</h1><p class=\"muted\">Aktualisiert: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC</p>");

        AppendCoaching(sb, report);

        if (latest is not null)
        {
            sb.Append($"<h2>Letzter Tag — {latest.Date}</h2><div class=\"grid\">");
            Tile(sb, "❤️ Ruhepuls", latest.RestingHeartRate, "bpm");
            Tile(sb, "💓 HRV", latest.HrvLastNight, "ms");
            Tile(sb, "😴 Schlaf", latest.SleepHours, "h");
            Tile(sb, "🔋 Body Battery", latest.BodyBatteryLow is null && latest.BodyBatteryHigh is null
                ? "–" : $"{latest.BodyBatteryLow?.ToString() ?? "?"}→{latest.BodyBatteryHigh?.ToString() ?? "?"}");
            Tile(sb, "😰 Stress Ø", latest.StressAvg, "");
            Tile(sb, "👟 Schritte", latest.Steps, "");
            Tile(sb, "🔥 Kalorien", latest.Calories, "kcal");
            Tile(sb, "⚡ Int.-Min.", latest.IntensityMinutes, "");
            sb.Append("</div>");
        }

        AppendCharts(sb, report);

        sb.Append("<h2>Verlauf</h2><table><thead><tr><th>Datum</th><th>Ruhe-HR</th><th>HRV</th><th>Schlaf</th><th>Schritte</th><th>Stress</th><th>Body Bat.</th><th>kcal</th></tr></thead><tbody>");
        foreach (var d in days)
        {
            var bb = d.BodyBatteryLow is null && d.BodyBatteryHigh is null
                ? "–" : $"{d.BodyBatteryLow?.ToString() ?? "?"}→{d.BodyBatteryHigh?.ToString() ?? "?"}";
            sb.Append($"<tr><td>{d.Date}</td><td>{C(d.RestingHeartRate)}</td><td>{C(d.HrvLastNight)}</td><td>{C(d.SleepHours)}</td><td>{C(d.Steps)}</td><td>{C(d.StressAvg)}</td><td>{bb}</td><td>{C(d.Calories)}</td></tr>");
        }
        sb.Append("</tbody></table>");

        var activities = report.Activities
            .OrderByDescending(a => a.Date, StringComparer.Ordinal)
            .ThenByDescending(a => a.Id)
            .Take(10)
            .ToList();
        if (activities.Count > 0)
        {
            sb.Append("<h2>Letzte Aktivitäten</h2><ul>");
            foreach (var a in activities)
            {
                var parts = new List<string>();
                if (a.DistanceKm is not null) parts.Add($"{a.DistanceKm} km");
                if (a.DurationMin is not null) parts.Add($"{a.DurationMin} min");
                if (a.Calories is not null) parts.Add($"{a.Calories} kcal");
                if (a.AverageHr is not null) parts.Add($"ø {a.AverageHr} bpm");
                var detail = parts.Count > 0 ? " — " + WebUtility.HtmlEncode(string.Join(", ", parts)) : "";
                var name = WebUtility.HtmlEncode(a.Name ?? a.Type ?? "Aktivität");
                var type = WebUtility.HtmlEncode(a.Type ?? "?");
                sb.Append($"<li><b>{a.Date}</b> {name} <span class=\"muted\">({type})</span>{detail}</li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendCoaching(StringBuilder sb, GarminReport report)
    {
        var c = report.Coaching;
        if (c is null) return;

        var (fg, bg) = c.Readiness switch
        {
            Readiness.Green => ("#1e7d34", "#e6f4ea"),
            Readiness.Amber => ("#8a6d00", "#fff7e6"),
            _ => ("#b3261e", "#fdecea"),
        };

        sb.Append($"<div style=\"border-left:5px solid {fg};background:{bg};color:#111;border-radius:.6rem;padding:.8rem 1rem;margin:1rem 0;\">");
        sb.Append($"<div style=\"font-size:1.2rem;font-weight:700;\">{WebUtility.HtmlEncode(c.Headline)}</div>");
        if (c.ReadinessScore is int sc)
            sb.Append($"<div style=\"color:#555;font-size:.85rem;\">Readiness {sc}{(c.ReadinessLevel is null ? "" : " (" + WebUtility.HtmlEncode(c.ReadinessLevel) + ")")}</div>");
        if (!string.IsNullOrWhiteSpace(report.CoachInsight))
            sb.Append($"<p style=\"margin:.5rem 0;\">{WebUtility.HtmlEncode(report.CoachInsight)}</p>");

        sb.Append("<ul style=\"margin:.4rem 0 0;padding-left:1.1rem;\">");
        foreach (var r in c.Rationale)
            sb.Append($"<li>{WebUtility.HtmlEncode(r)}</li>");
        if (c.PlanToday.Count > 0)
            sb.Append($"<li>📋 Plan heute: {WebUtility.HtmlEncode(string.Join(", ", c.PlanToday.Select(p => p.Title ?? p.Type.ToString())))}{(c.PlanNote is null ? "" : " — " + WebUtility.HtmlEncode(c.PlanNote))}</li>");
        else if (c.PlanNote is not null)
            sb.Append($"<li>📋 {WebUtility.HtmlEncode(c.PlanNote)}</li>");
        if (c.NextLongRun is not null)
            sb.Append($"<li>🛣️ Nächster Longrun: {c.NextLongRun.Date}</li>");
        if (c.NextQuality is not null)
            sb.Append($"<li>⚡ Nächste harte Einheit: {c.NextQuality.Date}</li>");

        var statusBits = new List<string>();
        if (c.TrainingStatus is not null) statusBits.Add($"Status: {c.TrainingStatus}");
        if (c.Acwr is double acwr) statusBits.Add($"ACWR {acwr:0.0}");
        if (c.Vo2Max is double v) statusBits.Add($"VO₂max {v:0.0}");
        if (statusBits.Count > 0) sb.Append($"<li>📈 {WebUtility.HtmlEncode(string.Join(" · ", statusBits))}</li>");

        if (c.RaceDate is not null)
        {
            var rp = $"🏁 Rennen: {c.RaceDate}" + (c.DaysToRace is int d ? $" (in {d} Tagen)" : "");
            if (c.Race?.MarathonSeconds is int ms) rp += $" · Marathon-Prognose {MarkdownRenderer.FormatTime(ms)}";
            if (!string.IsNullOrWhiteSpace(c.Goal)) rp += $" · Ziel {c.Goal}";
            sb.Append($"<li>{WebUtility.HtmlEncode(rp)}</li>");
        }
        else if (c.Race?.MarathonSeconds is int ms2)
        {
            sb.Append($"<li>🏁 Marathon-Prognose {MarkdownRenderer.FormatTime(ms2)}</li>");
        }
        if (c.TaperNote is not null) sb.Append($"<li>⏳ {WebUtility.HtmlEncode(c.TaperNote)}</li>");
        if (c.Nutrition is { } n)
        {
            var w = n.WeightKg is double kg ? $" (bei {kg} kg)" : "";
            sb.Append($"<li>🍽️ <b>Ernährung heute</b> ({WebUtility.HtmlEncode(n.DayType)}): ~{n.CalorieTarget} kcal — KH {n.CarbsG} g · Eiweiß {n.ProteinG} g · Fett {n.FatG} g{w}<br><span style=\"color:#555;\">{WebUtility.HtmlEncode(n.Guidance)}</span></li>");
        }
        sb.Append("</ul></div>");
    }

    private static void Tile(StringBuilder sb, string label, int? value, string unit) =>
        Tile(sb, label, value is null ? "–" : $"{value}{(unit.Length > 0 ? " " + unit : "")}");

    private static void Tile(StringBuilder sb, string label, double? value, string unit) =>
        Tile(sb, label, value is null ? "–" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}");

    private static void Tile(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class=\"tile\"><div class=\"k\">{label}</div><div class=\"v\">{WebUtility.HtmlEncode(value)}</div></div>");

    private static string C(int? v) => v?.ToString() ?? "–";
    private static string C(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "–";

    private static void AppendCharts(StringBuilder sb, GarminReport report)
    {
        var days = report.Days.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
        if (days.Count > 28) days = days.Skip(days.Count - 28).ToList();
        var labels = days.Select(d => d.Date).ToList();

        sb.Append("<h2>Entwicklung</h2><div class=\"charts\">");
        ChartCard(sb, "❤️ Ruhepuls (bpm)", SvgCharts.Line(labels, days.Select(d => (double?)d.RestingHeartRate).ToList(), "#ff453a", Avg(days, d => d.RestingHeartRate)));
        ChartCard(sb, "💓 HRV (ms)", SvgCharts.Line(labels, days.Select(d => (double?)d.HrvLastNight).ToList(), "#0a84ff"));
        ChartCard(sb, "😴 Schlaf (h)", SvgCharts.Bars(labels, days.Select(d => d.SleepHours).ToList(), "#30d158"));
        ChartCard(sb, "👟 Schritte", SvgCharts.Bars(labels, days.Select(d => (double?)d.Steps).ToList(), "#5e5ce6"));

        var (weekLabels, weekValues) = WeeklyKm(report.Activities);
        if (weekValues.Any(v => v.HasValue))
            ChartCard(sb, "🏃 Wochenkilometer (km)", SvgCharts.Bars(weekLabels, weekValues, "#ff9f0a"));

        sb.Append("</div>");
    }

    private static void ChartCard(StringBuilder sb, string title, string svg) =>
        sb.Append($"<div class=\"chart-card\"><div class=\"chart-title\">{title}</div>{svg}</div>");

    private static double? Avg(IReadOnlyList<DayMetrics> days, Func<DayMetrics, int?> selector)
    {
        var v = days.Select(selector).Where(x => x.HasValue).Select(x => (double)x!.Value).ToList();
        return v.Count > 0 ? v.Average() : null;
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
        if (ordered.Count > 10) ordered = ordered.Skip(ordered.Count - 10).ToList();
        return (
            ordered.Select(kv => "KW" + (kv.Key % 100).ToString("00")).ToList(),
            ordered.Select(kv => (double?)Math.Round(kv.Value, 1)).ToList());
    }
}
