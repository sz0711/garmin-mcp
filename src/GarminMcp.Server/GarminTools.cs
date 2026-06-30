using System.ComponentModel;
using Garmin.Connect.Models;
using GarminMcp.Core;
using ModelContextProtocol.Server;

namespace GarminMcp.Server;

/// <summary>
/// Read-only Garmin Connect MCP tools. Dates are ISO strings (<c>yyyy-MM-dd</c>).
/// Each tool delegates to <see cref="IGarminService"/> (resolved from DI).
/// </summary>
[McpServerToolType]
public static class GarminTools
{
    [McpServerTool(Name = "garmin_auth_status")]
    [Description("Check whether the server is signed in to Garmin. If NOT signed in, returns the setup URL the user should open in a browser to sign in (enter email/password + MFA and save). Call this first if other tools report not being signed in.")]
    public static object GetAuthStatus(IGarminConnectionProvider provider) => new
    {
        authenticated = provider.IsAuthenticated,
        setupUrl = provider.SetupUrl,
        message = provider.IsAuthenticated
            ? "Signed in to Garmin."
            : $"Not signed in to Garmin. Open {provider.SetupUrl} in a browser, sign in and save, then retry.",
    };

    [McpServerTool(Name = "garmin_get_profile")]
    [Description("Get the user's Garmin profile (name, display name, location, activity preferences).")]
    public static Task<GarminSocialProfile> GetProfile(IGarminService garmin, CancellationToken cancellationToken)
        => garmin.GetProfileAsync(cancellationToken);

    [McpServerTool(Name = "garmin_get_user_settings")]
    [Description("Get the user's account settings (unit system, measurement and display preferences).")]
    public static Task<GarminUserSettings> GetUserSettings(IGarminService garmin, CancellationToken cancellationToken)
        => garmin.GetUserSettingsAsync(cancellationToken);

    [McpServerTool(Name = "garmin_get_daily_summary")]
    [Description("Get the daily wellness summary for a day: steps, distance, calories, resting heart rate, stress, body battery and SpO2 highlights.")]
    public static Task<GarminStats> GetDailySummary(
        IGarminService garmin,
        [Description("Day in yyyy-MM-dd format")] string date,
        CancellationToken cancellationToken)
        => garmin.GetDailySummaryAsync(date, cancellationToken);

    [McpServerTool(Name = "garmin_get_steps")]
    [Description("Get the intraday step chart for a day.")]
    public static Task<GarminStepsData[]> GetSteps(
        IGarminService garmin,
        [Description("Day in yyyy-MM-dd format")] string date,
        CancellationToken cancellationToken)
        => garmin.GetStepsAsync(date, cancellationToken);

    [McpServerTool(Name = "garmin_get_heart_rate")]
    [Description("Get the intraday heart-rate data for a day.")]
    public static Task<GarminHr> GetHeartRate(
        IGarminService garmin,
        [Description("Day in yyyy-MM-dd format")] string date,
        CancellationToken cancellationToken)
        => garmin.GetHeartRateAsync(date, cancellationToken);

    [McpServerTool(Name = "garmin_get_sleep")]
    [Description("Get sleep data (stages, durations, scores) for the night of the given day.")]
    public static Task<GarminSleepData> GetSleep(
        IGarminService garmin,
        [Description("Day in yyyy-MM-dd format")] string date,
        CancellationToken cancellationToken)
        => garmin.GetSleepAsync(date, cancellationToken);

    [McpServerTool(Name = "garmin_get_body_battery")]
    [Description("Get the Body Battery time series for a date range. endDate defaults to startDate.")]
    public static Task<GarminBodyBatteryData[]> GetBodyBattery(
        IGarminService garmin,
        [Description("Start day in yyyy-MM-dd format")] string startDate,
        [Description("End day in yyyy-MM-dd format (optional; defaults to startDate)")] string? endDate,
        CancellationToken cancellationToken)
        => garmin.GetBodyBatteryAsync(startDate, endDate, cancellationToken);

    [McpServerTool(Name = "garmin_get_hrv")]
    [Description("Get the HRV (heart-rate variability) status report for a date range. endDate defaults to startDate.")]
    public static Task<GarminReportHrvStatus> GetHrv(
        IGarminService garmin,
        [Description("Start day in yyyy-MM-dd format")] string startDate,
        [Description("End day in yyyy-MM-dd format (optional; defaults to startDate)")] string? endDate,
        CancellationToken cancellationToken)
        => garmin.GetHrvAsync(startDate, endDate, cancellationToken);

    [McpServerTool(Name = "garmin_get_body_composition")]
    [Description("Get body composition (weight, body fat %, muscle/bone mass, BMI) for a date range. endDate defaults to startDate.")]
    public static Task<GarminBodyComposition> GetBodyComposition(
        IGarminService garmin,
        [Description("Start day in yyyy-MM-dd format")] string startDate,
        [Description("End day in yyyy-MM-dd format (optional; defaults to startDate)")] string? endDate,
        CancellationToken cancellationToken)
        => garmin.GetBodyCompositionAsync(startDate, endDate, cancellationToken);

    [McpServerTool(Name = "garmin_get_weight")]
    [Description("Get weigh-ins for a date range. endDate defaults to startDate.")]
    public static Task<GarminWeightRange> GetWeight(
        IGarminService garmin,
        [Description("Start day in yyyy-MM-dd format")] string startDate,
        [Description("End day in yyyy-MM-dd format (optional; defaults to startDate)")] string? endDate,
        CancellationToken cancellationToken)
        => garmin.GetWeightAsync(startDate, endDate, cancellationToken);

    [McpServerTool(Name = "garmin_get_hydration")]
    [Description("Get hydration (fluid intake) data for a day.")]
    public static Task<GarminHydrationData> GetHydration(
        IGarminService garmin,
        [Description("Day in yyyy-MM-dd format")] string date,
        CancellationToken cancellationToken)
        => garmin.GetHydrationAsync(date, cancellationToken);

    [McpServerTool(Name = "garmin_get_activities")]
    [Description("Get a page of the most recent activities (newest first).")]
    public static Task<GarminActivity[]> GetActivities(
        IGarminService garmin,
        [Description("Zero-based offset of the first activity (default 0)")] int start = 0,
        [Description("Number of activities to return, 1-100 (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
        => garmin.GetActivitiesAsync(start, limit, cancellationToken);

    [McpServerTool(Name = "garmin_get_activities_by_date")]
    [Description("Get activities within a date range, optionally filtered by activity type (e.g. running, cycling, swimming).")]
    public static Task<GarminActivity[]> GetActivitiesByDate(
        IGarminService garmin,
        [Description("Start day in yyyy-MM-dd format")] string startDate,
        [Description("End day in yyyy-MM-dd format")] string endDate,
        [Description("Optional activity type filter, e.g. 'running'")] string? activityType,
        CancellationToken cancellationToken)
        => garmin.GetActivitiesByDateAsync(startDate, endDate, activityType, cancellationToken);

    [McpServerTool(Name = "garmin_get_activity_details")]
    [Description("Get full details for a single activity by its numeric activity id.")]
    public static Task<GarminActivityDetails> GetActivityDetails(
        IGarminService garmin,
        [Description("Numeric Garmin activity id")] long activityId,
        CancellationToken cancellationToken)
        => garmin.GetActivityDetailsAsync(activityId, cancellationToken);

    [McpServerTool(Name = "garmin_get_personal_records")]
    [Description("Get the user's personal records (e.g. fastest 5K, longest run).")]
    public static Task<GarminPersonalRecord[]> GetPersonalRecords(IGarminService garmin, CancellationToken cancellationToken)
        => garmin.GetPersonalRecordsAsync(cancellationToken);
}
