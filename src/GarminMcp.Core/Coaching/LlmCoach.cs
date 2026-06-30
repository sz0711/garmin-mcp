using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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

    public async Task<string?> GenerateInsightAsync(DailyCoaching coaching, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new
            {
                model = _model,
                temperature = 0.5,
                max_tokens = 350,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = BuildUserPrompt(coaching) },
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
        "Du bist ein erfahrener, motivierender Lauf-/Marathon-Coach. Du bekommst die strukturierten " +
        "Tagesdaten eines Läufers (Erholung, Trainingslast, Trainingsplan-Einheit, Renn-Prognose). " +
        "Schreibe eine knappe, persönliche Tagesempfehlung auf Deutsch in 2–4 Sätzen: was heute zu tun ist " +
        "(Ruhetag/locker/moderat/hart), warum (der wichtigste Treiber), und ein kurzer Ausblick auf die " +
        "nächste Schlüsseleinheit. Respektiere die Empfehlung der Regel-Engine (widersprich der Ruhetag-/" +
        "Intensitäts-Vorgabe nicht), formuliere sie nur konkret und motivierend aus. Kein Markdown, keine " +
        "Aufzählung, keine Überschrift — nur Fließtext.";

    private static string BuildUserPrompt(DailyCoaching c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Datum: {c.Date}");
        sb.AppendLine($"Readiness: {c.Readiness}{(c.ReadinessScore is int s ? $" (Garmin {s}{(c.ReadinessLevel is null ? "" : "/" + c.ReadinessLevel)})" : "")}");
        sb.AppendLine($"Empfehlung der Regel-Engine: {c.Recommended} — {c.Headline}");
        if (c.Rationale.Count > 0) sb.AppendLine($"Begründung: {string.Join("; ", c.Rationale)}");
        if (c.Flags.Count > 0) sb.AppendLine($"Erholungs-Flags: {string.Join("; ", c.Flags)}");
        if (c.PlanToday.Count > 0) sb.AppendLine($"Plan heute: {string.Join(", ", c.PlanToday.Select(p => p.Title ?? p.Type.ToString()))}");
        if (c.PlanNote is not null) sb.AppendLine($"Plan-Hinweis: {c.PlanNote}");
        if (c.NextLongRun is not null) sb.AppendLine($"Nächster Longrun: {c.NextLongRun.Date} ({c.NextLongRun.Title ?? c.NextLongRun.Type.ToString()})");
        if (c.NextQuality is not null) sb.AppendLine($"Nächste harte Einheit: {c.NextQuality.Date} ({c.NextQuality.Title ?? c.NextQuality.Type.ToString()})");
        if (c.TrainingStatus is not null) sb.AppendLine($"Trainingsstatus: {c.TrainingStatus}");
        if (c.Acwr is double a) sb.AppendLine($"ACWR (Last-Verhältnis): {a:0.0}");
        if (c.Vo2Max is double v) sb.AppendLine($"VO2max: {v:0.0}");
        if (c.RaceDate is not null) sb.AppendLine($"Rennen: {c.RaceDate}{(c.DaysToRace is int d ? $" (in {d} Tagen)" : "")}");
        if (c.Race?.MarathonSeconds is int ms) sb.AppendLine($"Marathon-Prognose: {ms / 3600}:{(ms % 3600) / 60:00}:{ms % 60:00}");
        if (!string.IsNullOrWhiteSpace(c.Goal)) sb.AppendLine($"Ziel: {c.Goal}");
        if (c.TaperNote is not null) sb.AppendLine($"Taper-Kontext: {c.TaperNote}");
        return sb.ToString();
    }
}
