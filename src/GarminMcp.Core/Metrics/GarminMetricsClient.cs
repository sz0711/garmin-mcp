using System.Net;
using System.Text.Json;
using Garmin.Connect;
using Garmin.Connect.Auth.External;
using Garmin.Connect.Exceptions;

namespace GarminMcp.Core.Metrics;

/// <summary>Garmin Training Readiness snapshot (the daily 0-100 composite + drivers).</summary>
public sealed class TrainingReadiness
{
    public int? Score { get; set; }
    public string? Level { get; set; }            // e.g. READY / LOW / MODERATE / HIGH / PRIME
    public string? Feedback { get; set; }
    public int? SleepScore { get; set; }
    public int? HrvFactorPercent { get; set; }
    public int? RecoveryTimeHours { get; set; }
    public int? AcuteLoad { get; set; }
}

/// <summary>Garmin Training Status: phase, VO2max, acute/chronic load + ACWR, load focus.</summary>
public sealed class TrainingStatusInfo
{
    public string? StatusPhrase { get; set; }     // PRODUCTIVE_1 / MAINTAINING_1 / RECOVERY_1 / DETRAINING / ...
    public double? Vo2Max { get; set; }
    public int? WeeklyLoad { get; set; }
    public double? AcuteLoad { get; set; }
    public double? ChronicLoad { get; set; }
    public double? Acwr { get; set; }             // acute:chronic workload ratio
    public string? AcwrStatus { get; set; }
    public int? LoadAerobicLow { get; set; }
    public int? LoadAerobicHigh { get; set; }
    public int? LoadAnaerobic { get; set; }
    public string? LoadBalanceFeedback { get; set; }
}

/// <summary>Garmin race time predictions (seconds).</summary>
public sealed class RacePrediction
{
    public int? FiveKSeconds { get; set; }
    public int? TenKSeconds { get; set; }
    public int? HalfMarathonSeconds { get; set; }
    public int? MarathonSeconds { get; set; }
}

/// <summary>
/// Calls Garmin "metrics" endpoints that the typed library doesn't wrap, reusing the
/// authenticated <see cref="GarminConnectContext"/> (same token + auto-refresh). All
/// methods return null on any failure (missing data / 404 / parse error) so coaching
/// degrades gracefully.
/// </summary>
public sealed class GarminMetricsClient
{
    private readonly GarminConnectContext _context;

    public GarminMetricsClient(GarminConnectContext context) => _context = context;

    public async Task<TrainingReadiness?> GetTrainingReadinessAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync($"/metrics-service/metrics/trainingreadiness/{Iso(date)}", cancellationToken);
        if (doc is null) return null;
        var root = doc.RootElement;

        JsonElement entry;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return null;
            entry = root.EnumerateArray().FirstOrDefault(e =>
                e.TryGetProperty("inputContext", out var ic) && ic.ValueKind == JsonValueKind.String &&
                ic.GetString() == "AFTER_WAKEUP_RESET");
            if (entry.ValueKind != JsonValueKind.Object)
                entry = root[0];
        }
        else
        {
            entry = root;
        }
        if (entry.ValueKind != JsonValueKind.Object) return null;

        var recoveryMinutes = GetInt(entry, "recoveryTime");
        return new TrainingReadiness
        {
            Score = GetInt(entry, "score"),
            Level = GetString(entry, "level"),
            Feedback = GetString(entry, "feedbackShort") ?? GetString(entry, "feedbackLong"),
            SleepScore = GetInt(entry, "sleepScore"),
            HrvFactorPercent = GetInt(entry, "hrvFactorPercent"),
            RecoveryTimeHours = recoveryMinutes is int m ? (int)Math.Round(m / 60.0) : null,
            AcuteLoad = GetInt(entry, "acuteLoad"),
        };
    }

    public async Task<TrainingStatusInfo?> GetTrainingStatusAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync($"/metrics-service/metrics/trainingstatus/aggregated/{Iso(date)}", cancellationToken);
        if (doc is null) return null;
        var root = doc.RootElement;
        var info = new TrainingStatusInfo();

        if (TryProp(root, "mostRecentTrainingStatus", out var mrts) &&
            TryProp(mrts, "latestTrainingStatusData", out var lts) &&
            FirstObjectValue(lts) is JsonElement dev)
        {
            info.StatusPhrase = GetString(dev, "trainingStatusFeedbackPhrase");
            info.WeeklyLoad = GetInt(dev, "weeklyTrainingLoad");
            if (TryProp(dev, "acuteTrainingLoadDTO", out var atl))
            {
                info.AcuteLoad = GetDouble(atl, "dailyTrainingLoadAcute");
                info.ChronicLoad = GetDouble(atl, "dailyTrainingLoadChronic");
                info.Acwr = GetDouble(atl, "dailyAcuteChronicWorkloadRatio");
                info.AcwrStatus = GetString(atl, "acwrStatus");
            }
        }

        if (TryProp(root, "mostRecentVO2Max", out var vo2) && TryProp(vo2, "generic", out var generic))
            info.Vo2Max = GetDouble(generic, "vo2MaxValue") ?? GetDouble(generic, "vo2MaxPreciseValue");

        if (TryProp(root, "mostRecentTrainingLoadBalance", out var tlb) &&
            TryProp(tlb, "metricsTrainingLoadBalanceDTOMap", out var map) &&
            FirstObjectValue(map) is JsonElement bal)
        {
            info.LoadAerobicLow = GetInt(bal, "monthlyLoadAerobicLow");
            info.LoadAerobicHigh = GetInt(bal, "monthlyLoadAerobicHigh");
            info.LoadAnaerobic = GetInt(bal, "monthlyLoadAnaerobic");
            info.LoadBalanceFeedback = GetString(bal, "trainingBalanceFeedbackPhrase");
        }

        return info;
    }

    public async Task<RacePrediction?> GetRacePredictionsAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        using var doc = await GetJsonAsync($"/metrics-service/metrics/racepredictions/latest/{Uri.EscapeDataString(displayName)}", cancellationToken);
        if (doc is null) return null;
        var root = doc.RootElement;
        var e = root.ValueKind == JsonValueKind.Array
            ? (root.GetArrayLength() > 0 ? root[0] : default)
            : root;
        if (e.ValueKind != JsonValueKind.Object) return null;

        return new RacePrediction
        {
            FiveKSeconds = GetInt(e, "time5K"),
            TenKSeconds = GetInt(e, "time10K"),
            HalfMarathonSeconds = GetInt(e, "timeHalfMarathon"),
            MarathonSeconds = GetInt(e, "timeMarathon"),
        };
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _context.MakeHttpGet(path, null, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
        }
        catch (Exception ex) when (
            ex is GarminConnectRequestException or GarminConnectTooManyRequestsException
               or GarminConnectAuthenticationException or JsonException)
        {
            return null;
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static JsonElement? FirstObjectValue(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().Select(p => (JsonElement?)p.Value).FirstOrDefault()
            : null;

    private static int? GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble());
    }

    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
