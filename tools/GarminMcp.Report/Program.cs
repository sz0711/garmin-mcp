using System.Text.Json;
using GarminMcp.Core;
using GarminMcp.Core.Auth;
using GarminMcp.Core.Reporting;

// =====================================================================
// garmin-report — token-first dashboard generator (for GitHub Actions).
//
//   GARMIN_TOKEN=<base64>  dotnet run --project tools/GarminMcp.Report -- --days 30 --out .
//
// Writes/updates in the output folder:
//   data.json      full accumulated store (history is merged across runs)
//   dashboard.md   phone-friendly Markdown (GitHub mobile app)
//   index.html     self-contained HTML (open directly)
// =====================================================================

var days = GetIntArg(args, "--days") ?? ToInt(Environment.GetEnvironmentVariable("GARMIN_REPORT_DAYS")) ?? 30;
var outDir = GetArg(args, "--out") ?? Environment.GetEnvironmentVariable("GARMIN_REPORT_OUT") ?? ".";
var token = Environment.GetEnvironmentVariable("GARMIN_TOKEN");
var domain = Environment.GetEnvironmentVariable("GARMIN_DOMAIN") ?? "garmin.com";

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("ERROR: GARMIN_TOKEN is required (mint it with GarminMcp.Login).");
    return 2;
}

Directory.CreateDirectory(outDir);
var dataPath = Path.Combine(outDir, "data.json");

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
};

try
{
    using var http = GarminClientFactory.CreateHttpClient();
    var client = GarminClientFactory.CreateFromToken(GarminTokenBundle.Parse(token), http);
    var service = new GarminService(client);

    Console.Error.WriteLine($"[garmin-report] Fetching last {days} day(s) …");
    var fresh = await ReportBuilder.BuildAsync(service, days, DateOnly.FromDateTime(DateTime.Today), DateTimeOffset.UtcNow);

    GarminReport? existing = null;
    if (File.Exists(dataPath))
    {
        try { existing = JsonSerializer.Deserialize<GarminReport>(await File.ReadAllTextAsync(dataPath), jsonOptions); }
        catch (Exception ex) { Console.Error.WriteLine($"[garmin-report] Ignoring unreadable {dataPath}: {ex.Message}"); }
    }

    var merged = GarminReport.Merge(existing, fresh);
    var showDays = Math.Max(days, 14);

    await File.WriteAllTextAsync(dataPath, JsonSerializer.Serialize(merged, jsonOptions));
    await File.WriteAllTextAsync(Path.Combine(outDir, "dashboard.md"), MarkdownRenderer.Render(merged, showDays));
    await File.WriteAllTextAsync(Path.Combine(outDir, "index.html"), HtmlRenderer.Render(merged, showDays));

    var withData = merged.Days.Count(d => d.HasAnyData);
    Console.Error.WriteLine($"[garmin-report] OK — {merged.Days.Count} day(s) in store ({withData} with data), {merged.Activities.Count} activities. Wrote data.json, dashboard.md, index.html to {Path.GetFullPath(outDir)}");
    return 0;
}
catch (GarminServiceException ex)
{
    Console.Error.WriteLine($"[garmin-report] Garmin error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[garmin-report] FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int? GetIntArg(string[] args, string name) => ToInt(GetArg(args, name));
static int? ToInt(string? s) => int.TryParse(s, out var v) ? v : null;
