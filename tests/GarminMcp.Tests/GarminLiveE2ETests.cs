using GarminMcp.Core;
using GarminMcp.Core.Auth;
using Xunit;

namespace GarminMcp.Tests;

/// <summary>
/// Live end-to-end tests against the REAL Garmin Connect API, exercising the full
/// token-first path: stored OAuth1 token -> RefreshingTokenCache -> OAuth2 exchange
/// -> data calls via Unofficial.Garmin.Connect.
///
/// These only run when a real token is supplied via the GARMIN_E2E_TOKEN environment
/// variable (mint one with the GarminMcp.Login tool); otherwise they no-op so the
/// normal test suite stays hermetic.
///
///   $env:GARMIN_E2E_TOKEN = "<base64 token>"; dotnet test
/// </summary>
public class GarminLiveE2ETests
{
    private static string? Token => Environment.GetEnvironmentVariable("GARMIN_E2E_TOKEN");
    private static bool Enabled => !string.IsNullOrWhiteSpace(Token);

    [Fact]
    public async Task TokenFirst_FetchesRealProfile()
    {
        if (!Enabled)
            return; // skipped: set GARMIN_E2E_TOKEN to run

        using var http = GarminClientFactory.CreateHttpClient();
        var client = GarminClientFactory.CreateFromToken(GarminTokenBundle.Parse(Token!), http);
        var service = new GarminService(client);

        var profile = await service.GetProfileAsync();
        Assert.False(string.IsNullOrWhiteSpace(profile.DisplayName));
    }

    [Fact]
    public async Task TokenFirst_FetchesTodaysSummary()
    {
        if (!Enabled)
            return; // skipped: set GARMIN_E2E_TOKEN to run

        using var http = GarminClientFactory.CreateHttpClient();
        var client = GarminClientFactory.CreateFromToken(GarminTokenBundle.Parse(Token!), http);
        var service = new GarminService(client);

        var summary = await service.GetDailySummaryAsync(DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.NotNull(summary);
    }
}
