namespace GarminMcp.Core;

/// <summary>
/// Resolves "today" in the athlete's timezone. The container/runner clock is UTC, so near
/// midnight a naive <c>DateTime.Today</c> would roll the training day over too early/late.
/// Defaults to Europe/Berlin; override with the <c>GARMIN_TZ</c> environment variable.
/// </summary>
public static class LocalDate
{
    public static DateOnly Today(string? timeZoneId = null)
    {
        timeZoneId ??= Environment.GetEnvironmentVariable("GARMIN_TZ") ?? "Europe/Berlin";
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
        }
        catch
        {
            return DateOnly.FromDateTime(DateTime.UtcNow);
        }
    }
}
