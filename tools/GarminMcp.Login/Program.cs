using Garmin.Connect.Auth.External;
using GarminMcp.Core.Auth;

// =====================================================================
// garmin-login — mints a long-lived, password-free GARMIN_TOKEN.
//
// Run it ONCE on a trusted machine. It performs the Garmin SSO login
// (email/password + MFA if your account has it) and prints the
// GARMIN_TOKEN you put into the MCP / Docker configuration. The running
// container then never needs your password or MFA again (~1 year).
//
// Usage:
//   $env:GARMIN_EMAIL="you@example.com"; $env:GARMIN_PASSWORD="..."
//   dotnet run --project tools/GarminMcp.Login [-- <output-file>]
// =====================================================================

string? email = Environment.GetEnvironmentVariable("GARMIN_EMAIL");
string? password = Environment.GetEnvironmentVariable("GARMIN_PASSWORD");
var domain = Environment.GetEnvironmentVariable("GARMIN_DOMAIN") ?? "garmin.com";
var outputFile = args.Length > 0 ? args[0] : null;

if (string.IsNullOrWhiteSpace(email))
{
    Console.Error.Write("Garmin email: ");
    email = Console.ReadLine()?.Trim();
}
if (string.IsNullOrWhiteSpace(password))
{
    Console.Error.Write("Garmin password (visible): ");
    password = Console.ReadLine();
}
if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
{
    Console.Error.WriteLine("ERROR: email and password are required (env GARMIN_EMAIL / GARMIN_PASSWORD).");
    return 2;
}

using var http = GarminClientFactory.CreateHttpClient();
var sso = new GarminSsoClient(http, domain);
var mfa = new DelegateMfaCodeProvider(() =>
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(">>> MFA required. Enter the code from your authenticator / email and press Enter:");
    Console.Error.Write("MFA code: ");
    return Console.ReadLine()?.Trim() ?? string.Empty;
});

try
{
    Console.Error.WriteLine("[garmin-login] Authenticating with Garmin SSO …");
    var result = await sso.LoginAsync(email!, password!, mfa);
    var bundle = new GarminTokenBundle { Oauth1 = result.Oauth1, Oauth2 = result.Oauth2 };

    // Verify the minted token actually works (password-free path).
    Console.Error.WriteLine("[garmin-login] Verifying the token by fetching your profile …");
    var client = GarminClientFactory.CreateFromToken(bundle, http);
    var profile = await client.GetSocialProfile();
    Console.Error.WriteLine($"[garmin-login] OK — authenticated as {profile.DisplayName} ({profile.FullName}).");

    var token = bundle.ToBase64();

    if (!string.IsNullOrWhiteSpace(outputFile))
    {
        await File.WriteAllTextAsync(outputFile, bundle.ToJson());
        Console.Error.WriteLine($"[garmin-login] Token bundle written to {outputFile}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("=== Your GARMIN_TOKEN (KEEP SECRET — grants full account access) ===");
    // The token itself goes to STDOUT so it can be captured/piped cleanly.
    Console.WriteLine(token);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Use it in claude_desktop_config.json, e.g.:");
    Console.Error.WriteLine("""
        {
          "mcpServers": {
            "garmin": {
              "command": "docker",
              "args": ["run", "-i", "--rm", "-e", "GARMIN_TOKEN", "garmin-mcp:latest"],
              "env": { "GARMIN_TOKEN": "<paste the token above>" }
            }
          }
        }
        """);
    return 0;
}
catch (GarminConnectAuthenticationException ex)
{
    Console.Error.WriteLine($"[garmin-login] AUTH FAILED (Code={ex.Code}): {ex.Message}");
    if (ex.Code == Code.MfaBlockedCloudflare)
        Console.Error.WriteLine("[garmin-login] Cloudflare blocked the login. Try again later or from a different network.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[garmin-login] FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
