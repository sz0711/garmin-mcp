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
        DailyCoaching coaching, IReadOnlyList<DayMetrics>? recentDays = null,
        IReadOnlyList<HealthAlert>? alerts = null, CancellationToken cancellationToken = default)
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
                    new { role = "user", content = BuildUserPrompt(coaching, recentDays, alerts) },
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

    /// <summary>
    /// Writes a short German weekly review + outlook (recap of the last completed week and the
    /// focus for the coming one). Generated once per week (Mondays). Returns null on any failure.
    /// </summary>
    public async Task<string?> GenerateWeeklyReviewAsync(
        DailyCoaching coaching, WeeklyStats lastWeek, IReadOnlyList<DayMetrics>? recentDays = null,
        PacingAnalysis? pacing = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                model = _model,
                temperature = 0.5,
                max_tokens = 400,
                messages = new object[]
                {
                    new { role = "system", content = WeeklySystemPrompt },
                    new { role = "user", content = BuildWeeklyPrompt(coaching, lastWeek, recentDays, pacing) },
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
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content) ? null : content!.Trim();
        }
        catch
        {
            return null;
        }
    }

    private const string WeeklySystemPrompt =
        "Du bist ein erfahrener, datengetriebener Marathon-Coach. Schreibe einen kompakten, persönlichen " +
        "Wochen-Rückblick auf Deutsch (4–6 Sätze, Fließtext, KEIN Markdown, KEINE Aufzählung):\n" +
        "1) Würdige die letzte Woche ehrlich (Umfang, Planerfüllung, Schlüsseleinheiten, Erholungstrend).\n" +
        "2) Benenne EINE konkrete Sache, die gut lief, und EINE, an der du diese Woche arbeiten würdest — " +
        "ist eine Pacing-Analyse des letzten Longruns angegeben, greife sie auf, wenn sie eine sinnvolle " +
        "konkrete Sache liefert (z. B. Fade/Positive Split ansprechen, oder kardiales Driften einordnen).\n" +
        "3) Gib einen klaren Fokus/Ausblick für die kommende Woche, abgestimmt auf Form, Erholung und Zielrennen.\n" +
        "Sei motivierend, konkret und ehrlich; übertreibe nicht und beschönige Warnsignale nicht.";

    private static string BuildWeeklyPrompt(DailyCoaching c, WeeklyStats w, IReadOnlyList<DayMetrics>? recentDays, PacingAnalysis? pacing = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Stichtag (Wochenstart): {c.Date}");
        sb.AppendLine($"Letzte Woche: {w.Km:0.#} km, {w.Sessions} Einheiten, längster Lauf {w.LongestKm:0.#} km, {w.Hours:0.#} h, {w.IntensityMinutes} Intensitätsminuten.");
        if (c.PlannedLastWeek is int pl && pl > 0)
            sb.AppendLine($"Planerfüllung letzte Woche: {c.DoneLastWeek ?? 0}/{pl} Einheiten{(c.PlannedKmLastWeek is double pk && pk > 0 ? $", {c.DoneKmLastWeek ?? 0:0.#}/{pk:0.#} km" : "")}.");
        if (pacing is not null)
        {
            var verdictLabel = pacing.Verdict switch
            {
                // Deliberately "zweite Hälfte", not "2. Hälfte" -- a bare ordinal digit here would
                // survive WeeklyReviewFactCheck's date/percent filters as a spurious plain number if
                // the LLM echoes this phrasing back (which it's explicitly asked to do).
                SplitVerdict.Negative => "Negative Split (zweite Hälfte schneller als die erste)",
                SplitVerdict.Even => "gleichmäßig verteilt",
                _ => "Positive Split/Fade (zweite Hälfte langsamer als die erste)",
            };
            sb.AppendLine($"Letzter Longrun ({pacing.ActivityDate}, {pacing.DistanceKm:0.#} km): {verdictLabel}, {Math.Abs(pacing.PercentDifference):0.#}% Unterschied zwischen den Hälften" +
                (pacing.AerobicDecouplingPercent is double drift ? $", kardiales Driften {drift:0.#}%." : "."));
        }
        if (recentDays is not null)
        {
            var rows = recentDays.OrderByDescending(d => d.Date, StringComparer.Ordinal).Take(7)
                .Select(d => $"  {d.Date}: RuheHR {N(d.RestingHeartRate)}, HRV {N(d.HrvLastNight)}, Schlaf {N(d.SleepHours)}h").ToList();
            if (rows.Count > 0) { sb.AppendLine("Erholung (letzte Tage):"); foreach (var r in rows) sb.AppendLine(r); }
        }
        if (c.Ctl is double ctl && c.Atl is double atl && c.Tsb is double tsb)
            sb.AppendLine($"Form: Fitness/CTL {ctl:0}, Fatigue/ATL {atl:0}, Form/TSB {tsb:+0;-0;0}.");
        if (c.PlannedThisWeek is int pt && pt > 0) sb.AppendLine($"Diese Woche geplant: {pt} Einheiten.");
        if (c.NextLongRun is not null) sb.AppendLine($"Nächster Longrun: {c.NextLongRun.Date} ({c.NextLongRun.Title ?? "Long Run"}).");
        if (c.NextQuality is not null) sb.AppendLine($"Nächste harte Einheit: {c.NextQuality.Date} ({c.NextQuality.Title ?? "Quality"}).");
        if (c.RaceDate is not null) sb.AppendLine($"Zielrennen: {c.RaceDate}{(c.DaysToRace is int d ? $" (in {d} Tagen)" : "")}{(string.IsNullOrWhiteSpace(c.Goal) ? "" : $", Ziel {c.Goal}")}.");
        if (!string.IsNullOrWhiteSpace(c.EnduranceCaveat)) sb.AppendLine($"Vorbehalt zur Marathon-Prognose: {c.EnduranceCaveat}");
        return sb.ToString();
    }

    private const string SystemPrompt =
        "Du bist ein erfahrener, datengetriebener Marathon-Coach (Methodik: HRV-gesteuertes Training nach " +
        "Plews/Buchheit, Acute:Chronic-Workload, polarisiertes Training, sauberer Taper). Du erhältst die " +
        "strukturierten Tagesdaten eines Läufers. Schreibe eine prägnante, persönliche Tagesempfehlung auf " +
        "Deutsch (4–8 Sätze, Fließtext, KEIN Markdown, KEINE Aufzählung, KEINE Überschrift):\n" +
        "1) Was heute konkret zu tun ist. Halte dich STRIKT an die vorgegebene 'Empfohlene Einheit' " +
        "(Ruhetag/locker/moderat/hart) — schwäche sie nicht ab und mache aus einem Ruhe-/Erholungstag nichts " +
        "Härteres. Ist eine strukturierte Einheit geplant und die Readiness lässt sie zu, nenne die konkreten " +
        "Eckdaten (Distanz/Dauer, Pace/HR aus der Struktur), ggf. mit Anpassung. WENN HEUTE BEREITS EINE EINHEIT " +
        "ABSOLVIERT WURDE (Feld 'Heute bereits trainiert'), erkenne das ausdrücklich an und richte die Empfehlung " +
        "auf Regeneration, Auffüllen und höchstens lockeres Auslaufen/Mobilität aus — verordne KEINE zweite harte " +
        "Einheit. Gibt es ein 'Zieltempo heute', nenne es konkret.\n" +
        "2) Der wichtigste Grund (EIN Treiber: HRV-Trend, Ruhepuls, Schlaf, Body Battery oder Trainingslast).\n" +
        "3) Kurzer Ausblick auf die nächste Schlüsseleinheit/den Longrun, sodass heute darauf hinarbeitet.\n" +
        "4) Eine konkrete Ernährungsempfehlung für heute: nenne 2–3 passende Lebensmittel/Mahlzeiten, die zu den " +
        "vorgegebenen Makro-Zielen (Kalorien, KH/Eiweiß/Fett) und zur Tagesbelastung passen, inkl. Timing um die " +
        "Einheit (z. B. Carbs davor/danach, Eiweiß zur Regeneration). Nenne die Makro-Zielwerte nicht stur erneut, " +
        "sondern setze sie in praktische Mahlzeiten um.\n" +
        "Sei motivierend und konkret, aber ehrlich bei Warnsignalen (Krankheitsmuster → Ruhe).";

    private static string BuildUserPrompt(DailyCoaching c, IReadOnlyList<DayMetrics>? recentDays, IReadOnlyList<HealthAlert>? alerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Datum: {c.Date}");
        sb.AppendLine($"Readiness-Bewertung: {c.Readiness}{(c.ReadinessScore is int s ? $"; Garmin Training Readiness {s}{(c.ReadinessLevel is null ? "" : "/" + c.ReadinessLevel)}" : "")}");
        sb.AppendLine($"Empfohlene Einheit (Leitplanke, NICHT abschwächen): {c.Recommended} — {c.Headline}");
        if (c.CompletedToday.Count > 0)
            sb.AppendLine($"Heute bereits trainiert: {string.Join(", ", c.CompletedToday.Select(a => $"{a.Name ?? a.Type ?? "Lauf"}{(a.DistanceKm is double km ? $" {km:0.#} km" : "")}{(a.DurationMin is double m ? $" / {m:0} min" : "")}"))}");
        if (!string.IsNullOrWhiteSpace(c.TodayTargetPace))
            sb.AppendLine($"Zieltempo heute: {c.TodayTargetPace}");
        if (alerts is not null)
        {
            var actionable = alerts.Where(a => a.Level is AlertLevel.Red or AlertLevel.Amber).Select(a => a.Title).ToList();
            if (actionable.Count > 0) sb.AppendLine($"Warnsignale (Frühwarnsystem): {string.Join("; ", actionable)}");
        }
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
        if (c.OnTrackForGoal is bool ot)
            sb.AppendLine($"Zielabgleich: Marathon-Prognose ist {(ot ? "auf Kurs (schneller als Ziel)" : "noch über der Zielzeit")}{(c.GoalGapSeconds is int g ? $", Differenz {(g <= 0 ? "-" : "+")}{Math.Abs(g) / 60} min" : "")}.");
        if (!string.IsNullOrWhiteSpace(c.EnduranceCaveat))
            sb.AppendLine($"Wichtiger Vorbehalt zur Prognose: {c.EnduranceCaveat} Erwähne das, wenn du die Prognose/den Zielabgleich ansprichst — verkaufe \"auf Kurs\" nicht als ausdauerseitig bestätigt, wenn das nicht zutrifft.");
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
