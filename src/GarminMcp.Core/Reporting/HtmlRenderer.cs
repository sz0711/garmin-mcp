using System.Globalization;
using System.Net;
using System.Text;

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
        var chrono = days.AsEnumerable().Reverse().ToList();
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
              .spark{display:flex;align-items:center;gap:.6rem;margin:.4rem 0;} .spark .lbl{width:6rem;color:var(--muted);font-size:.8rem;}
              ul{padding-left:1.1rem;} li{margin:.25rem 0;}
            </style></head><body>
            """);

        sb.Append($"<h1>🏃 Garmin Dashboard</h1><p class=\"muted\">Aktualisiert: {report.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC</p>");

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

        sb.Append($"<h2>Trends (letzte {chrono.Count} Tage)</h2>");
        SparkRow(sb, "Ruhepuls", chrono.Select(d => (double?)d.RestingHeartRate), "#ff453a");
        SparkRow(sb, "HRV", chrono.Select(d => (double?)d.HrvLastNight), "#0a84ff");
        SparkRow(sb, "Schlaf", chrono.Select(d => d.SleepHours), "#30d158");

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

    private static void Tile(StringBuilder sb, string label, int? value, string unit) =>
        Tile(sb, label, value is null ? "–" : $"{value}{(unit.Length > 0 ? " " + unit : "")}");

    private static void Tile(StringBuilder sb, string label, double? value, string unit) =>
        Tile(sb, label, value is null ? "–" : $"{value.Value.ToString(CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}");

    private static void Tile(StringBuilder sb, string label, string value) =>
        sb.Append($"<div class=\"tile\"><div class=\"k\">{label}</div><div class=\"v\">{WebUtility.HtmlEncode(value)}</div></div>");

    private static string C(int? v) => v?.ToString() ?? "–";
    private static string C(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "–";

    private static void SparkRow(StringBuilder sb, string label, IEnumerable<double?> values, string color)
    {
        sb.Append($"<div class=\"spark\"><span class=\"lbl\">{label}</span>{Svg(values.ToList(), color)}</div>");
    }

    private static string Svg(IReadOnlyList<double?> values, string color)
    {
        const int w = 280, h = 44, pad = 4;
        var present = values.Select((v, i) => (i, v)).Where(t => t.v.HasValue).Select(t => (t.i, val: t.v!.Value)).ToList();
        if (present.Count == 0)
            return $"<svg width=\"{w}\" height=\"{h}\"></svg>";

        double min = present.Min(p => p.val), max = present.Max(p => p.val);
        double n = Math.Max(1, values.Count - 1);
        string X(int i) => (pad + i / n * (w - 2 * pad)).ToString("0.#", CultureInfo.InvariantCulture);
        string Y(double v) => (max > min
            ? pad + (1 - (v - min) / (max - min)) * (h - 2 * pad)
            : h / 2.0).ToString("0.#", CultureInfo.InvariantCulture);

        var pts = string.Join(" ", present.Select(p => $"{X(p.i)},{Y(p.val)}"));
        var dots = string.Concat(present.Select(p => $"<circle cx=\"{X(p.i)}\" cy=\"{Y(p.val)}\" r=\"1.6\" fill=\"{color}\"/>"));
        return $"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\"><polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\" points=\"{pts}\"/>{dots}</svg>";
    }
}
