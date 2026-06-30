using System.Text;
using ModelContextProtocol.Client;
using Xunit;

namespace GarminMcp.Tests;

/// <summary>
/// Spawns the real MCP server over stdio (with a syntactically valid but fake token,
/// so startup succeeds without hitting Garmin) and verifies the MCP handshake and that
/// all read-only Garmin tools are advertised with the expected schema.
/// </summary>
public class McpServerIntegrationTests
{
    private static readonly string[] ExpectedTools =
    {
        "garmin_auth_status",
        "garmin_get_profile",
        "garmin_get_user_settings",
        "garmin_get_daily_summary",
        "garmin_get_steps",
        "garmin_get_heart_rate",
        "garmin_get_sleep",
        "garmin_get_body_battery",
        "garmin_get_hrv",
        "garmin_get_body_composition",
        "garmin_get_weight",
        "garmin_get_hydration",
        "garmin_get_activities",
        "garmin_get_activities_by_date",
        "garmin_get_activity_details",
        "garmin_get_personal_records",
        "garmin_daily_coaching",
        "garmin_training_readiness",
        "garmin_training_status",
        "garmin_race_predictions",
        "garmin_scheduled_workouts",
    };

    [Fact]
    public async Task Server_StartsOverStdio_AndAdvertisesAllReadOnlyTools()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "garmin-test",
            Command = "dotnet",
            Arguments = new List<string> { FindServerDll() },
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["GARMIN_TOKEN"] = FakeToken(),
                ["MCP_TRANSPORT"] = "stdio",
                ["GARMIN_SETUP_ENABLED"] = "false",
            },
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        foreach (var expected in ExpectedTools)
            Assert.Contains(expected, names);
    }

    [Fact]
    public async Task DailySummaryTool_DeclaresRequiredDateParameter()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "garmin-test",
            Command = "dotnet",
            Arguments = new List<string> { FindServerDll() },
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["GARMIN_TOKEN"] = FakeToken(),
                ["MCP_TRANSPORT"] = "stdio",
                ["GARMIN_SETUP_ENABLED"] = "false",
            },
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        var summary = tools.Single(t => t.Name == "garmin_get_daily_summary");

        var schema = summary.JsonSchema.GetRawText();
        Assert.Contains("date", schema);
    }

    private static string FakeToken()
    {
        const string json = "{\"oauth1\":{\"oauth_token\":\"x\",\"oauth_token_secret\":\"y\",\"domain\":\"garmin.com\"}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string FindServerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(Path.Combine(dir.FullName, "GarminMcp.slnx"))
               && !File.Exists(Path.Combine(dir.FullName, "GarminMcp.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate repo root (GarminMcp.slnx/.sln).");

        var config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        var dll = Path.Combine(dir.FullName, "src", "GarminMcp.Server", "bin", config, "net9.0", "GarminMcp.Server.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Server build not found at {dll}. Build the solution first.", dll);
        return dll;
    }
}
