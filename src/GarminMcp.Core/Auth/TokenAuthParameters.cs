using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>
/// Minimal <see cref="IAuthParameters"/> for token-first mode. Data requests in
/// Unofficial.Garmin.Connect only use <see cref="BaseUrl"/> (and optional cookies);
/// the SSO-only members are never reached because our <see cref="RefreshingTokenCache"/>
/// always supplies a valid OAuth2 token, so the library never starts its own login.
///
/// If the library ever DOES fall back to a full login (e.g. the OAuth1 token was
/// revoked and a request 401s), <see cref="GetFormParameters"/> throws a clear error
/// instead of silently failing — there is no password in this mode.
/// </summary>
public sealed class TokenAuthParameters : IAuthParameters
{
    public string UserAgent { get; }
    public string Domain { get; }
    public string Cookies { get; set; } = string.Empty;
    public string Csrf { get; set; } = string.Empty;
    public string BaseUrl => $"https://connect.{Domain}";
    public ConsumerCredentials ConsumerCredentials { get; }

    public TokenAuthParameters(string domain = "garmin.com", string? userAgent = null, ConsumerCredentials? consumer = null)
    {
        Domain = domain;
        UserAgent = userAgent ?? new StaticUserAgent().New;
        ConsumerCredentials = consumer ?? ConsumerCredentials.Default;
    }

    public IReadOnlyDictionary<string, string> GetHeaders() => new Dictionary<string, string>
    {
        ["User-Agent"] = UserAgent,
        ["origin"] = $"https://sso.{Domain}",
    };

    public IReadOnlyDictionary<string, string> GetFormParameters() => throw new InvalidOperationException(
        "Token-first mode has no password to perform an interactive Garmin login. " +
        "The stored token is likely expired or revoked — re-mint it with the login tool.");

    public IReadOnlyDictionary<string, string> GetQueryParameters() => new Dictionary<string, string>
    {
        ["id"] = "gauth-widget",
        ["embedWidget"] = "true",
    };

    public IReadOnlyDictionary<string, string> GetMfaParameters() => new Dictionary<string, string>
    {
        ["embed"] = "true",
        ["fromPage"] = "setupEnterMfaCode",
        ["_csrf"] = Csrf,
    };
}
