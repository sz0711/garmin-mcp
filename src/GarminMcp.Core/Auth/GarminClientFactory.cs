using System.Net;
using System.Security.Authentication;
using Garmin.Connect;
using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>
/// Builds <see cref="IGarminConnectClient"/> instances. Every runtime path ends up on
/// the token-first <see cref="RefreshingTokenCache"/> so the running service is always
/// password-free; credentials (if given) are only used once to mint the OAuth1 token.
/// </summary>
public static class GarminClientFactory
{
    /// <summary>
    /// Creates an HttpClient configured the way the Garmin SSO/data flow needs:
    /// no auto-redirect (the flow inspects 302 Location headers manually), TLS 1.2/1.3,
    /// and decompression. This is the exact configuration validated end-to-end.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        return new HttpClient(handler);
    }

    /// <summary>
    /// Token-first: build the shared <see cref="GarminConnectContext"/> (auth + token
    /// refresh). Reused for both the typed client and raw metric endpoint calls.
    /// </summary>
    public static GarminConnectContext CreateContextFromToken(GarminTokenBundle bundle, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(httpClient);

        var sso = new GarminSsoClient(httpClient, bundle.Oauth1.Domain);
        var cache = new RefreshingTokenCache(bundle.Oauth1, sso, bundle.Oauth2);
        var auth = new TokenAuthParameters(bundle.Oauth1.Domain);
        return new GarminConnectContext(httpClient, auth, new NotImplementedMfaCode(), cache);
    }

    /// <summary>Build a typed data client over an existing context (shares auth + token cache).</summary>
    public static IGarminConnectClient CreateClient(GarminConnectContext context) => new GarminConnectClient(context);

    /// <summary>Token-first: build a typed data client from a stored credential bundle.</summary>
    public static IGarminConnectClient CreateFromToken(GarminTokenBundle bundle, HttpClient httpClient)
        => new GarminConnectClient(CreateContextFromToken(bundle, httpClient));

    /// <summary>
    /// Mints a fresh token bundle from email/password (+ MFA if required) and returns
    /// both the ready data client and the bundle (persist it to go password-free).
    /// </summary>
    public static async Task<(IGarminConnectClient Client, GarminTokenBundle Bundle)> CreateFromCredentialsAsync(
        string email, string password, string domain, IMfaCodeProvider mfaCodeProvider,
        HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        var sso = new GarminSsoClient(httpClient, domain);
        var result = await sso.LoginAsync(email, password, mfaCodeProvider, cancellationToken);
        var bundle = new GarminTokenBundle { Oauth1 = result.Oauth1, Oauth2 = result.Oauth2 };
        return (CreateFromToken(bundle, httpClient), bundle);
    }
}
