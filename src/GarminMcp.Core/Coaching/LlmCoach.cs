using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Core.Coaching;

/// <summary>
/// Turns the structured <see cref="DailyCoaching"/> into a short natural-language daily
/// coach note using GitHub Models (OpenAI-compatible inference). In GitHub Actions this
/// authenticates with the built-in GITHUB_TOKEN (permission <c>models: read</c>) — no
/// separate API key, no Copilot subscription. Returns null on any failure so the
/// dashboard falls back to the deterministic rule-based text.
/// </summary>
public sealed class LlmCoach
{
    public const string DefaultEndpoint = "https://models.github.ai/inference/chat/completions";
    public const string DefaultModel = "openai/gpt-4o-mini";

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _endpoint;
    private readonly string _model;

    public LlmCoach(HttpClient http, string token, string? endpoint = null, string? model = null)
    {
        _http = http;
        _token = token;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint!;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
    }

    public async Task<string?> GenerateInsightAsync(
        DailyCoaching coaching, IReadOnlyList<DayMetrics>? recentDays = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                model = _model,
                temperature = 0.5,
                max_tokens = 450,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = BuildUserPrompt(coaching, recentDays) },
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.UserAgent.ParseAdd("garmin-mcp-coach/1.0");
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(content) ? null : content!.Trim();
        }
        catch
        {
            return null;
        }
    }

    private const string SystemPrompt =
        "Du bist ein erfahrener, datengetriebener Marathon-Coach (Methodik: HRV-gesteuertes Training nach " +
        "Plews/Buchheit, Acute:Chronic-Workload, polarisiertes Training, sauberer Taper). Du erhältst die " +
        "strukturierten Tagesdaten eines Läufers. Schreibe eine prägnante, persönliche Tagesempfehlung auf " +
        "Deutsch (4–8 Sätze, Fließtext, KEIN Markdown, KEINE Aufzählung, KEINE Überschrift):\n" +
        "1) Was heute konkret zu tun ist. Halte dich STRIKT an die vorgegebene 'Empfohlene Einheit' " +
        "(Ruhetag/locker/moderat/hart) — schwäche sie nicht ab und mache aus einem Ruhe-/Erholungstag nichts " +
        "Härteres. Ist eine strukturierte Einheit geplant und die Readiness lässt sie zu, nenne die konkreten " +
        "Eckdaten (Distanz/Dauer, Pace/HR aus der Struktur), ggf. mit Anpassung.\n" +
        "2) Der wichtigste Grund (EIN Treiber: HRV-Trend, Ruhepuls, Schlaf, Body Battery oder Trainingslast).\n" +
        "3) Kurzer Ausblick auf die nächste Schlüsseleinheit/den Longrun, sodass heute darauf hinarbeitet.\n" +
        "4) Eine konkrete Ernährungsempfehlung für heute: nenne 2–3 passende Lebensmittel/Mahlzeiten, die zu den " +
        "vorgegebenen Makro-Zielen (Kalorien, KH/Eiweiß/Fett) und zur Tagesbelastung passen, inkl. Timing um die " +
        "Einheit (z. B. Carbs davor/danach, Eiweiß zur Regeneration). Nenne die Makro-Zielwerte nicht stur erneut, " +
        "sondern setze sie in praktische Mahlzeiten um.\n" +
        "Sei motivierend und konkret, aber ehrlich bei Warnsignalen (Krankheitsmuster → Ruhe).";

    private static string BuildUserPrompt(DailyCoaching c, IReadOnlyList<DayMetrics>? recentDays)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Datum: {c.Date}");
        sb.AppendLine($"Readiness-Bewertung: {c.Readiness}{(c.ReadinessScore is int s ? $"; Garmin Training Readiness {s}{(c.ReadinessLevel is null ? "" : "/" + c.ReadinessLevel)}" : "")}");
        sb.AppendLine($"Empfohlene Einheit (Leitplanke, NICHT abschwächen): {c.Recommended} — {c.Headline}");
        if (c.Flags.Count > 0) sb.AppendLine($"Erholungs-Flags: {string.Join("; ", c.Flags)}");
        if (c.Rationale.Count > 0) sb.AppendLine($"Kontext: {string.Join("; ", c.Rationale)}");

        if (recentDays is not null)
        {
            var rows = recentDays
                .OrderByDescending(d => d.Date, StringComparer.Ordinal)
                .Take(7)
                .Select(d => $"  {d.Date}: HRV {N(d.HrvLastNight)}, RuheHR {N(d.RestingHeartRate)}, Schlaf {N(d.SleepHours)}h, BodyBattery {N(d.BodyBatteryHigh)}, Schritte {N(d.Steps)}")
                .ToList();
            if (rows.Count > 0)
            {
                sb.AppendLine("Letzte Tage:");
                foreach (var r in rows) sb.AppendLine(r);
            }
        }

        if (c.TrainingStatus is not null) sb.AppendLine($"Trainingsstatus: {c.TrainingStatus}");
        if (c.Acwr is double a) sb.AppendLine($"ACWR (Last-Verhältnis, Ziel 0.8–1.3): {a:0.0}");
        if (c.Vo2Max is double v) sb.AppendLine($"VO2max: {v:0.0}");

        if (c.PlanToday.Count > 0)
        {
            sb.AppendLine("Geplante Einheit(en) heute:");
            foreach (var p in c.PlanToday) sb.AppendLine($"  - {p.Detail ?? p.Title ?? p.Type.ToString()}");
        }
        else
        {
            sb.AppendLine("Heute keine Einheit im Plan.");
        }
        if (c.PlanNote is not null) sb.AppendLine($"Plan-Abgleich (Regel-Engine): {c.PlanNote}");
        if (c.NextLongRun is not null) sb.AppendLine($"Nächster Longrun: {c.NextLongRun.Date} — {c.NextLongRun.Detail ?? c.NextLongRun.Title ?? "Long Run"}");
        if (c.NextQuality is not null) sb.AppendLine($"Nächste harte Einheit: {c.NextQuality.Date} — {c.NextQuality.Detail ?? c.NextQuality.Title ?? "Quality"}");

        if (c.RaceDate is not null) sb.AppendLine($"Rennen: {c.RaceDate}{(c.DaysToRace is int d ? $" (in {d} Tagen)" : "")}");
        if (c.Race?.MarathonSeconds is int ms) sb.AppendLine($"Marathon-Prognose: {ms / 3600}:{(ms % 3600) / 60:00}:{ms % 60:00}");
        if (!string.IsNullOrWhiteSpace(c.Goal)) sb.AppendLine($"Zielzeit: {c.Goal}");
        if (c.TaperNote is not null) sb.AppendLine($"Taper-Kontext: {c.TaperNote}");
        if (c.Nutrition is { } n)
            sb.AppendLine($"Makro-Ziel heute ({n.DayType}): ~{n.CalorieTarget} kcal — Kohlenhydrate {n.CarbsG} g, Eiweiß {n.ProteinG} g, Fett {n.FatG} g{(n.WeightKg is double w ? $" (Gewicht {w} kg)" : "")}.");
        if (c.Ctl is double ctl && c.Atl is double atl && c.Tsb is double tsb)
            sb.AppendLine($"Form (Performance-Management): Fitness/CTL {ctl:0}, Fatigue/ATL {atl:0}, Form/TSB {tsb:+0;-0;0} (positiv = frisch, negativ = ermüdet).");
        if (c.PlannedThisWeek is int pl && pl > 0)
            sb.AppendLine($"Plan-Adhärenz diese Woche: {c.DoneThisWeek ?? 0}/{pl} Einheiten erledigt.");
        if (c.SleepConsistencyMin is double scm)
            sb.AppendLine($"Schlaf-Konsistenz: ±{scm:0} min Zubettgeh-Variabilität (kleiner = besser).");
        return sb.ToString();
    }

    private static string N(int? x) => x?.ToString() ?? "–";
    private static string N(double? x) => x?.ToString("0.#", CultureInfo.InvariantCulture) ?? "–";
}
