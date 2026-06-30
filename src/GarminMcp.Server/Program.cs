using GarminMcp.Core;
using GarminMcp.Core.Auth;
using GarminMcp.Server;
using Microsoft.Extensions.Logging;

// Transport: "stdio" (default, for Claude Desktop via `docker run -i`) or "http"
// (Streamable HTTP + REST). A small browser-based sign-in UI runs in BOTH modes on a
// dedicated port so the user can sign in / re-mint the token without the CLI.
var transport = (Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio").Trim().ToLowerInvariant();
var options = GarminEnv.Load();
var httpClient = GarminClientFactory.CreateHttpClient();

var setupEnabled = !string.Equals(Environment.GetEnvironmentVariable("GARMIN_SETUP_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);
var setupPort = int.TryParse(Environment.GetEnvironmentVariable("GARMIN_SETUP_PORT"), out var p) ? p : 8765;
var setupUrl = Environment.GetEnvironmentVariable("GARMIN_SETUP_URL") ?? $"http://localhost:{setupPort}/";

var provider = new GarminConnectionProvider(
    httpClient, options, setupUrl, warn: m => Console.Error.WriteLine($"[garmin-mcp] {m}"));
await provider.InitializeAsync();

// Setup UI server (dedicated port; stderr-only logging so stdio JSON-RPC stays clean).
WebApplication? setupApp = null;
if (setupEnabled)
{
    var setupBuilder = WebApplication.CreateBuilder();
    setupBuilder.Logging.ClearProviders();
    setupBuilder.WebHost.UseUrls($"http://0.0.0.0:{setupPort}");
    setupBuilder.Services.AddSingleton<IGarminConnectionProvider>(provider);
    setupApp = setupBuilder.Build();
    SetupWebUi.Map(setupApp);
    await setupApp.StartAsync();
    Console.Error.WriteLine($"[garmin-mcp] Sign-in UI listening on {setupUrl}");
}

if (transport is "http" or "https")
{
    await RunHttpAsync(args, provider);
}
else
{
    await RunStdioAsync(args, provider);
}

if (setupApp is not null)
    await setupApp.StopAsync();

static async Task RunStdioAsync(string[] args, IGarminConnectionProvider provider)
{
    var builder = Host.CreateApplicationBuilder(args);

    // CRITICAL for stdio: stdout carries JSON-RPC, so all logs must go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton(provider);
    builder.Services.AddSingleton(provider.Service);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args, IGarminConnectionProvider provider)
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSingleton(provider);
    builder.Services.AddSingleton(provider.Service);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    app.Use(async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (GarminNotAuthenticatedException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
        }
        catch (ArgumentException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
        }
        catch (GarminServiceException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway).ExecuteAsync(context);
        }
    });

    app.MapMcp();
    GarminRestApi.Map(app);

    await app.RunAsync();
}
