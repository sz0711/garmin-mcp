using GarminMcp.Core;

namespace GarminMcp.Server;

/// <summary>Loads <see cref="GarminOptions"/> from the simple GARMIN_* environment variables.</summary>
public static class GarminEnv
{
    public static GarminOptions Load() => new()
    {
        Token = Get("GARMIN_TOKEN"),
        Email = Get("GARMIN_EMAIL"),
        Password = Get("GARMIN_PASSWORD"),
        MfaCode = Get("GARMIN_MFA_CODE"),
        Domain = Get("GARMIN_DOMAIN") ?? "garmin.com",
        TokenFile = Get("GARMIN_TOKEN_FILE"),
    };

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
