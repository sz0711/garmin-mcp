using Garmin.Connect;
using Garmin.Connect.Auth;
using GarminMcp.Core.Auth;

namespace GarminMcp.Core;

/// <summary>
/// Resolves a ready-to-use <see cref="IGarminConnectClient"/> from
/// <see cref="GarminOptions"/>, preferring token-first (password-free) and falling
/// back to a one-time credential login that persists a token.
/// </summary>
public static class GarminConnection
{
    public static async Task<GarminConnectionResult> ResolveAsync(
        GarminOptions options, HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);

        // 1) Explicit token (recommended): GARMIN_TOKEN.
        if (options.HasToken)
        {
            var bundle = GarminTokenBundle.Parse(options.Token!);
            return new(GarminClientFactory.CreateFromToken(bundle, httpClient), bundle, CredentialSource.Token);
        }

        // 2) Persisted token file (e.g. a Docker volume) from a previous login.
        if (!string.IsNullOrWhiteSpace(options.TokenFile) && File.Exists(options.TokenFile))
        {
            var bundle = GarminTokenBundle.Parse(await File.ReadAllTextAsync(options.TokenFile, cancellationToken));
            return new(GarminClientFactory.CreateFromToken(bundle, httpClient), bundle, CredentialSource.TokenFile);
        }

        // 3) Email/password: mint a token once, persist it if a file is configured.
        if (options.HasCredentials)
        {
            IMfaCodeProvider mfa = string.IsNullOrWhiteSpace(options.MfaCode)
                ? new DelegateMfaCodeProvider((Func<string>)(() => throw new GarminServiceException(
                    "Garmin requires an MFA code, but none was provided and the server cannot prompt interactively. " +
                    "Mint a token once with the login tool and set GARMIN_TOKEN instead.")))
                : new StaticMfaCodeProvider(options.MfaCode!);

            var (client, bundle) = await GarminClientFactory.CreateFromCredentialsAsync(
                options.Email!, options.Password!, options.Domain, mfa, httpClient, cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.TokenFile))
                await File.WriteAllTextAsync(options.TokenFile, bundle.ToJson(), cancellationToken);

            return new(client, bundle, CredentialSource.Credentials);
        }

        throw new GarminServiceException(
            "No Garmin credentials configured. Set GARMIN_TOKEN (recommended) or GARMIN_EMAIL + GARMIN_PASSWORD.");
    }
}

public sealed record GarminConnectionResult(
    IGarminConnectClient Client, GarminTokenBundle Bundle, CredentialSource Source);

public enum CredentialSource
{
    Token,
    TokenFile,
    Credentials,
}
