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

        GarminTokenBundle bundle;
        CredentialSource source;

        if (options.HasToken)
        {
            // 1) Explicit token (recommended): GARMIN_TOKEN.
            bundle = GarminTokenBundle.Parse(options.Token!);
            source = CredentialSource.Token;
        }
        else if (!string.IsNullOrWhiteSpace(options.TokenFile) && File.Exists(options.TokenFile))
        {
            // 2) Persisted token file (e.g. a Docker volume) from a previous login.
            bundle = GarminTokenBundle.Parse(await File.ReadAllTextAsync(options.TokenFile, cancellationToken));
            source = CredentialSource.TokenFile;
        }
        else if (options.HasCredentials)
        {
            // 3) Email/password: mint a token once, persist it if a file is configured.
            IMfaCodeProvider mfa = string.IsNullOrWhiteSpace(options.MfaCode)
                ? new DelegateMfaCodeProvider((Func<string>)(() => throw new GarminServiceException(
                    "Garmin requires an MFA code, but none was provided and the server cannot prompt interactively. " +
                    "Mint a token once with the login tool and set GARMIN_TOKEN instead.")))
                : new StaticMfaCodeProvider(options.MfaCode!);

            var sso = new GarminSsoClient(httpClient, options.Domain);
            var login = await sso.LoginAsync(options.Email!, options.Password!, mfa, cancellationToken);
            bundle = new GarminTokenBundle { Oauth1 = login.Oauth1, Oauth2 = login.Oauth2 };
            if (!string.IsNullOrWhiteSpace(options.TokenFile))
                await File.WriteAllTextAsync(options.TokenFile, bundle.ToJson(), cancellationToken);
            source = CredentialSource.Credentials;
        }
        else
        {
            throw new GarminServiceException(
                "No Garmin credentials configured. Set GARMIN_TOKEN (recommended) or GARMIN_EMAIL + GARMIN_PASSWORD.");
        }

        var context = GarminClientFactory.CreateContextFromToken(bundle, httpClient);
        return new GarminConnectionResult(GarminClientFactory.CreateClient(context), bundle, source, context);
    }
}

public sealed record GarminConnectionResult(
    IGarminConnectClient Client, GarminTokenBundle Bundle, CredentialSource Source, GarminConnectContext Context);

public enum CredentialSource
{
    Token,
    TokenFile,
    Credentials,
}
