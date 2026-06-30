using System.Globalization;
using Garmin.Connect;
using Garmin.Connect.Auth.External;
using Garmin.Connect.Exceptions;
using Garmin.Connect.Models;

namespace GarminMcp.Core;

/// <summary>
/// Default <see cref="IGarminService"/> over an <see cref="IGarminConnectClient"/>.
/// Parses ISO dates, applies sensible guards, and turns library exceptions into
/// clean <see cref="GarminServiceException"/> messages.
/// </summary>
public sealed class GarminService : IGarminService
{
    public const string DateFormat = "yyyy-MM-dd";

    private readonly Func<IGarminConnectClient> _clientResolver;
    private string? _displayName;

    /// <summary>Fixed client (used by tests and the simple/static case).</summary>
    public GarminService(IGarminConnectClient client) : this(() => client) { }

    /// <summary>
    /// Resolver-based client, so the underlying connection can change at runtime
    /// (e.g. after a browser-based login) or be unavailable until then.
    /// </summary>
    public GarminService(Func<IGarminConnectClient> clientResolver) => _clientResolver = clientResolver;

    private IGarminConnectClient Client => _clientResolver();

    public Task<GarminSocialProfile> GetProfileAsync(CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var profile = await Client.GetSocialProfile(cancellationToken);
            _displayName = profile.DisplayName;
            return profile;
        }, "user profile");

    public Task<GarminUserSettings> GetUserSettingsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetUserSettings(cancellationToken), "user settings");

    public Task<GarminStats> GetDailySummaryAsync(string date, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetUserSummary(ParseDate(date, nameof(date)), cancellationToken), "daily summary");

    public Task<GarminStepsData[]> GetStepsAsync(string date, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetWellnessStepsData(ParseDate(date, nameof(date)), cancellationToken), "steps");

    public Task<GarminHr> GetHeartRateAsync(string date, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetWellnessHeartRates(ParseDate(date, nameof(date)), cancellationToken), "heart rate");

    public Task<GarminSleepData> GetSleepAsync(string date, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetWellnessSleepData(ParseDate(date, nameof(date)), cancellationToken), "sleep");

    public Task<GarminBodyBatteryData[]> GetBodyBatteryAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            var (start, end) = ParseRange(startDate, endDate);
            return Client.GetWelnessBodyBatteryData(start, end, cancellationToken);
        }, "body battery");

    public Task<GarminReportHrvStatus> GetHrvAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            var (start, end) = ParseRange(startDate, endDate);
            return Client.GetReportHrvStatus(start, end, cancellationToken);
        }, "HRV");

    public Task<GarminBodyComposition> GetBodyCompositionAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            var (start, end) = ParseRange(startDate, endDate);
            return Client.GetBodyComposition(start, end, cancellationToken);
        }, "body composition");

    public Task<GarminWeightRange> GetWeightAsync(string startDate, string? endDate = null, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            var (start, end) = ParseRange(startDate, endDate);
            return Client.GetWeightRange(start, end, cancellationToken);
        }, "weight");

    public Task<GarminHydrationData> GetHydrationAsync(string date, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetHydrationData(ParseDate(date, nameof(date)), cancellationToken), "hydration");

    public Task<GarminActivity[]> GetActivitiesAsync(int start = 0, int limit = 20, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            if (start < 0)
                throw new ArgumentException("'start' must be >= 0.", nameof(start));
            if (limit is < 1 or > 100)
                throw new ArgumentException("'limit' must be between 1 and 100.", nameof(limit));
            return Client.GetActivities(start, limit, cancellationToken);
        }, "activities");

    public Task<GarminActivity[]> GetActivitiesByDateAsync(string startDate, string endDate, string? activityType = null, CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            var (start, end) = ParseRange(startDate, endDate);
            return Client.GetActivitiesByDate(start, end, activityType ?? string.Empty, cancellationToken);
        }, "activities by date");

    public Task<GarminActivityDetails> GetActivityDetailsAsync(long activityId, CancellationToken cancellationToken = default) =>
        Guard(() => Client.GetActivityDetails(activityId, cancellationToken: cancellationToken), "activity details");

    public Task<GarminPersonalRecord[]> GetPersonalRecordsAsync(CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            var displayName = _displayName ?? (await Client.GetSocialProfile(cancellationToken)).DisplayName;
            _displayName = displayName;
            return await Client.GetPersonalRecord(displayName, cancellationToken);
        }, "personal records");

    private static DateTime ParseDate(string date, string paramName)
    {
        if (DateTime.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        throw new ArgumentException($"'{paramName}' must be a date in {DateFormat} format, got '{date}'.", paramName);
    }

    private static (DateTime Start, DateTime End) ParseRange(string startDate, string? endDate)
    {
        var start = ParseDate(startDate, nameof(startDate));
        var end = string.IsNullOrWhiteSpace(endDate) ? start : ParseDate(endDate, nameof(endDate));
        if (end < start)
            throw new ArgumentException($"endDate ({endDate}) must not be before startDate ({startDate}).");
        return (start, end);
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action, string what)
    {
        try
        {
            return await action();
        }
        catch (GarminConnectTooManyRequestsException)
        {
            throw new GarminServiceException($"Garmin rate-limited the request ({what}, HTTP 429). Please retry later.");
        }
        catch (GarminConnectAuthenticationException ex)
        {
            throw new GarminServiceException(
                $"Garmin authentication failed ({what}): {ex.Message}. The token may be expired or revoked — re-mint it with the login tool.", ex);
        }
        catch (GarminConnectRequestException ex)
        {
            throw new GarminServiceException(
                $"Garmin request failed ({what}): HTTP {(int)ex.Status} {ex.Status}.", ex);
        }
    }
}
