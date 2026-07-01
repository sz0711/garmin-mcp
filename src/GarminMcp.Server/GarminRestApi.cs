using System.Globalization;
using GarminMcp.Core;
using GarminMcp.Core.Coaching;
using GarminMcp.Core.Reporting;

namespace GarminMcp.Server;

/// <summary>
/// Plain REST surface, mirroring the MCP tools. Only mapped in HTTP transport mode.
/// Read-only; dates are ISO <c>yyyy-MM-dd</c> query parameters.
/// </summary>
public static class GarminRestApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("/api/garmin");

        api.MapGet("/profile", (IGarminService g, CancellationToken ct) => g.GetProfileAsync(ct));
        api.MapGet("/user-settings", (IGarminService g, CancellationToken ct) => g.GetUserSettingsAsync(ct));
        api.MapGet("/daily-summary", (IGarminService g, string date, CancellationToken ct) => g.GetDailySummaryAsync(date, ct));
        api.MapGet("/steps", (IGarminService g, string date, CancellationToken ct) => g.GetStepsAsync(date, ct));
        api.MapGet("/heart-rate", (IGarminService g, string date, CancellationToken ct) => g.GetHeartRateAsync(date, ct));
        api.MapGet("/sleep", (IGarminService g, string date, CancellationToken ct) => g.GetSleepAsync(date, ct));
        api.MapGet("/body-battery", (IGarminService g, string startDate, string? endDate, CancellationToken ct) => g.GetBodyBatteryAsync(startDate, endDate, ct));
        api.MapGet("/hrv", (IGarminService g, string startDate, string? endDate, CancellationToken ct) => g.GetHrvAsync(startDate, endDate, ct));
        api.MapGet("/body-composition", (IGarminService g, string startDate, string? endDate, CancellationToken ct) => g.GetBodyCompositionAsync(startDate, endDate, ct));
        api.MapGet("/weight", (IGarminService g, string startDate, string? endDate, CancellationToken ct) => g.GetWeightAsync(startDate, endDate, ct));
        api.MapGet("/hydration", (IGarminService g, string date, CancellationToken ct) => g.GetHydrationAsync(date, ct));
        api.MapGet("/activities", (IGarminService g, int? start, int? limit, CancellationToken ct) => g.GetActivitiesAsync(start ?? 0, limit ?? 20, ct));
        api.MapGet("/activities-by-date", (IGarminService g, string startDate, string endDate, string? activityType, CancellationToken ct) => g.GetActivitiesByDateAsync(startDate, endDate, activityType, ct));
        api.MapGet("/activities/{activityId:long}", (IGarminService g, long activityId, CancellationToken ct) => g.GetActivityDetailsAsync(activityId, ct));
        api.MapGet("/personal-records", (IGarminService g, CancellationToken ct) => g.GetPersonalRecordsAsync(ct));

        // Coaching. "Today" is resolved via LocalDate.Today() (timezone-aware, see LocalDate.cs) —
        // not DateTime.Today — so the REST surface agrees with the MCP tools on which calendar day
        // is "today" near local midnight, instead of using the container's naive/UTC clock.
        api.MapGet("/coaching", async (IGarminConnectionProvider p, CancellationToken ct) =>
        {
            if (!p.IsAuthenticated) return Results.Problem(p.SetupUrl, statusCode: StatusCodes.Status401Unauthorized);
            var goal = Environment.GetEnvironmentVariable("GARMIN_GOAL");
            var r = await ReportBuilder.BuildAsync(p.Service, 14, LocalDate.Today(), DateTimeOffset.UtcNow, p.Metrics, goal, ct);
            return Results.Json((object?)r.Coaching ?? new { message = "No coaching available yet." });
        });
        api.MapGet("/health-alerts", async (IGarminConnectionProvider p, CancellationToken ct) =>
        {
            if (!p.IsAuthenticated) return Results.Problem(p.SetupUrl, statusCode: StatusCodes.Status401Unauthorized);
            var r = await ReportBuilder.BuildAsync(p.Service, 30, LocalDate.Today(), DateTimeOffset.UtcNow, p.Metrics, null, ct);
            return Results.Json(new { alerts = r.Alerts });
        });
        api.MapGet("/training-trends", async (IGarminConnectionProvider p, CancellationToken ct) =>
        {
            if (!p.IsAuthenticated) return Results.Problem(p.SetupUrl, statusCode: StatusCodes.Status401Unauthorized);
            var today = LocalDate.Today();
            var r = await ReportBuilder.BuildAsync(p.Service, 35, today, DateTimeOffset.UtcNow, p.Metrics, null, ct);
            return Results.Json(TrainingTrends.Compute(r, today));
        });
        api.MapGet("/scheduled-workouts", (IGarminConnectionProvider p, CancellationToken ct) =>
            TrainingPlanReader.BuildAsync(p.Service, LocalDate.Today(), ct));
        api.MapGet("/training-readiness", async (IGarminConnectionProvider p, string date, CancellationToken ct) =>
            (object?)(p.Metrics is null ? null : await p.Metrics.GetTrainingReadinessAsync(Day(date), ct)) ?? new { message = "no data" });
        api.MapGet("/training-status", async (IGarminConnectionProvider p, string date, CancellationToken ct) =>
            (object?)(p.Metrics is null ? null : await p.Metrics.GetTrainingStatusAsync(Day(date), ct)) ?? new { message = "no data" });
        api.MapGet("/race-predictions", async (IGarminConnectionProvider p, CancellationToken ct) =>
        {
            if (p.Metrics is null) return Results.Json(new { message = "not signed in" });
            var profile = await p.Service.GetProfileAsync(ct);
            return Results.Json((object?)await p.Metrics.GetRacePredictionsAsync(profile.DisplayName, ct) ?? new { message = "no data" });
        });
    }

    private static DateOnly Day(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new ArgumentException($"date must be yyyy-MM-dd, got '{date}'.", nameof(date));
}
