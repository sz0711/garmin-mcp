using Garmin.Connect.Models;

namespace GarminMcp.Core;

/// <summary>
/// App-facing, read-only Garmin Connect operations. Dates are ISO strings
/// (<c>yyyy-MM-dd</c>). Implementations translate, call Garmin, and normalize errors
/// into <see cref="GarminServiceException"/>. This is the single surface used by both
/// the MCP tools and the REST endpoints.
/// </summary>
public interface IGarminService
{
    /// <summary>The user's social/public profile (name, display name, etc.).</summary>
    Task<GarminSocialProfile> GetProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>Account/user settings (units, measurement system, preferences).</summary>
    Task<GarminUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Daily wellness summary for a day: steps, calories, resting HR, stress, body battery, SpO2, etc.</summary>
    Task<GarminStats> GetDailySummaryAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>Intraday step chart for a day.</summary>
    Task<GarminStepsData[]> GetStepsAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>Intraday heart-rate data for a day.</summary>
    Task<GarminHr> GetHeartRateAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>Sleep data (stages, durations, scores) for the night of the given day.</summary>
    Task<GarminSleepData> GetSleepAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>Body Battery time series over a date range.</summary>
    Task<GarminBodyBatteryData[]> GetBodyBatteryAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>HRV (heart-rate variability) status report over a date range.</summary>
    Task<GarminReportHrvStatus> GetHrvAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>Body composition (weight, body fat, muscle mass, etc.) over a date range.</summary>
    Task<GarminBodyComposition> GetBodyCompositionAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>Weigh-ins over a date range.</summary>
    Task<GarminWeightRange> GetWeightAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>Hydration data for a day.</summary>
    Task<GarminHydrationData> GetHydrationAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>A page of recent activities (newest first).</summary>
    Task<GarminActivity[]> GetActivitiesAsync(int start = 0, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Activities within a date range, optionally filtered by activity type (e.g. "running").</summary>
    Task<GarminActivity[]> GetActivitiesByDateAsync(string startDate, string endDate, string? activityType = null, CancellationToken cancellationToken = default);

    /// <summary>Full details for a single activity.</summary>
    Task<GarminActivityDetails> GetActivityDetailsAsync(long activityId, CancellationToken cancellationToken = default);

    /// <summary>The user's personal records.</summary>
    Task<GarminPersonalRecord[]> GetPersonalRecordsAsync(CancellationToken cancellationToken = default);

    /// <summary>Calendar for the week containing the given date (incl. scheduled plan workouts).</summary>
    Task<GarminCalendarWeek> GetCalendarWeekAsync(string date, CancellationToken cancellationToken = default);

    /// <summary>Full structure of a workout (segments/steps/targets) by its id.</summary>
    Task<GarminWorkout> GetWorkoutAsync(long workoutId, CancellationToken cancellationToken = default);

    /// <summary>The user's registered gear (shoes, bikes, etc.) as Garmin returns it — metadata
    /// (make/model/type, registration/retirement dates) plus <c>MaximumMeters</c>, which is the
    /// user-configured "remind me after X km" wear threshold, NOT actual accumulated mileage. Garmin
    /// exposes real cumulative distance per item only via a separate gear-stats endpoint this API
    /// does not cover, so it is not available here.</summary>
    Task<GarminGear[]> GetUserGearsAsync(CancellationToken cancellationToken = default);
}
