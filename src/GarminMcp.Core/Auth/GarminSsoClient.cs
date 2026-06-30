using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Garmin.Connect.Auth;
using Garmin.Connect.Auth.External;

namespace GarminMcp.Core.Auth;

/// <summary>
/// Garmin SSO + OAuth client. This is a faithful, self-contained port of the auth
/// flow that Unofficial.Garmin.Connect performs internally — the difference being
/// that it RETURNS the long-lived OAuth1 token (the library keeps it private), so we
/// can persist it and operate token-first (password-free) afterwards.
///
/// Flow: SSO embed (cookies) -> signin page (CSRF) -> credential POST (+ optional MFA)
/// -> service ticket -> OAuth1 token (preauthorized) -> OAuth2 token (exchange).
/// The OAuth2 token is then refreshed from the OAuth1 token alone, indefinitely.
///
/// Requires an <see cref="HttpClient"/> configured with AllowAutoRedirect = false
/// (the flow inspects 302 Location headers manually). Use
/// <see cref="GarminClientFactory.CreateHttpClient"/>.
/// </summary>
public sealed class GarminSsoClient
{
    private readonly HttpClient _httpClient;
    private readonly string _domain;
    private readonly string _userAgent;
    private readonly ConsumerCredentials _consumer;

    private string? _cookies;
    private string? _csrf;

    public GarminSsoClient(HttpClient httpClient, string domain = "garmin.com",
        string? userAgent = null, ConsumerCredentials? consumer = null)
    {
        _httpClient = httpClient;
        _domain = domain;
        _userAgent = userAgent ?? new StaticUserAgent().New;
        _consumer = consumer ?? ConsumerCredentials.Default;
    }

    private string SsoUrl => $"https://sso.{_domain}/sso";
    private string EmbedUrl => $"{SsoUrl}/embed";
    private string SigninUrl => $"{SsoUrl}/signin";
    private string MfaCodeUrl => $"{SsoUrl}/verifyMFA/loginEnterMfaCode";

    private static readonly Regex CsrfRegex =
        new("name=\"_csrf\"\\s+value=\"(?<csrf>.+?)\"", RegexOptions.Compiled);
    private static readonly Regex TicketRegex =
        new("embed\\?ticket=([^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Full interactive login. Returns both tokens. <paramref name="mfaCodeProvider"/>
    /// is invoked only if the account requires MFA.
    /// </summary>
    public async Task<GarminAuthResult> LoginAsync(
        string email, string password, IMfaCodeProvider mfaCodeProvider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty.", nameof(email));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        _cookies = await RequestCookiesAsync(cancellationToken);
        _csrf = await RequestCsrfTokenAsync(cancellationToken);
        var ticket = await GetOAuthTicketAsync(email, password, mfaCodeProvider, cancellationToken);
        var oauth1 = await GetOAuth1TokenAsync(ticket, cancellationToken);
        var oauth2 = await ExchangeOAuth2Async(oauth1, cancellationToken);
        return new GarminAuthResult(oauth1, oauth2);
    }

    /// <summary>
    /// Mints a fresh OAuth2 access token from a stored OAuth1 token. No password, no
    /// MFA, no SSO — this is the password-free refresh used by the running service.
    /// </summary>
    public async Task<OAuth2Token> ExchangeOAuth2Async(OAuth1Token oauth1, CancellationToken cancellationToken = default)
    {
        var url = $"https://connectapi.{_domain}/oauth-service/oauth/exchange/user/2.0";
        var auth = OAuth1Signer.BuildAuthorizationHeader(
            "POST", url, _consumer.ConsumerKey, _consumer.ConsumerSecret, oauth1.Token, oauth1.TokenSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GarminConnectAuthenticationException(
                $"OAuth2 exchange failed ({(int)response.StatusCode} {response.StatusCode}): {body}")
            { Code = Code.OAuth2TokenNotFound };

        var token = JsonSerializer.Deserialize<OAuth2Token>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new GarminConnectAuthenticationException(
                "OAuth2 exchange returned no access token. The OAuth1 token may be expired or revoked.")
            { Code = Code.OAuth2TokenNotFound };
        return token;
    }

    private async Task<string> RequestCookiesAsync(CancellationToken cancellationToken)
    {
        var url = BuildUrl(EmbedUrl, MergeQuery(BaseQuery, ("gauthHost", SsoUrl)));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddSsoHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new GarminConnectAuthenticationException("Failed to fetch cookies from Garmin.")
            { Code = Code.CookiesNotFound };

        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            throw new GarminConnectAuthenticationException("No Set-Cookie returned by Garmin.")
            { Code = Code.CookiesNotFound };

        var cookies = string.Concat(setCookies.Select(c => c + ";"));
        if (string.IsNullOrWhiteSpace(cookies))
            throw new GarminConnectAuthenticationException("Found cookies but they are empty.")
            { Code = Code.CookiesNotFound };
        return cookies;
    }

    private async Task<string> RequestCsrfTokenAsync(CancellationToken cancellationToken)
    {
        var url = BuildUrl(SigninUrl, EmbedQuery());
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddSsoHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new GarminConnectAuthenticationException("Failed to fetch CSRF token from Garmin.")
            { Code = Code.CsrfTokenNotFound };

        return FindCsrf(await response.Content.ReadAsStringAsync(cancellationToken), Code.CsrfTokenNotFound);
    }

    private async Task<string> GetOAuthTicketAsync(
        string email, string password, IMfaCodeProvider mfaCodeProvider, CancellationToken cancellationToken)
    {
        var url = BuildUrl(SigninUrl, EmbedQuery());

        HttpStatusCode status;
        string? location;
        string body;
        var attempt = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddSsoHeaders(request);
            request.Headers.TryAddWithoutValidation("referer", SigninUrl);
            request.Headers.TryAddWithoutValidation("NK", "NT");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["embed"] = "true",
                ["_csrf"] = _csrf ?? string.Empty,
                ["username"] = email,
                ["password"] = password,
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            status = response.StatusCode;
            location = response.Headers.Location?.ToString();
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (status != HttpStatusCode.TooManyRequests || ++attempt >= 5)
                break;
            await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken);
        }

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new GarminConnectAuthenticationException(
                $"Garmin authentication failed ({(int)status} {status}): {body}")
            { Code = Code.OAuth1TicketNotFound };

        var mfaViaRedirect = status == HttpStatusCode.Found && location is not null && location.Contains(MfaCodeUrl);
        var mfaViaPage = status == HttpStatusCode.OK && body.Contains("validateMfaCodeAndPrivacyConsents()");
        if (mfaViaRedirect || mfaViaPage)
        {
            if (mfaViaRedirect)
                body = await HandleRedirectAsync(location!, cancellationToken, 0);

            _csrf = FindCsrf(body, Code.CsrfTokenNotFound);
            var code = await mfaCodeProvider.GetMfaCodeAsync();
            if (string.IsNullOrWhiteSpace(code))
                throw new GarminConnectAuthenticationException("MFA code provided is empty.")
                { Code = Code.MfaInvalidCode };
            body = await CompleteMfaAsync(code, cancellationToken);
        }

        var match = TicketRegex.Match(body);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            throw new GarminConnectAuthenticationException("Failed to find the service ticket in Garmin's response.")
            { Code = Code.OAuth1TicketNotFound };
        return match.Groups[1].Value;
    }

    private async Task<string> CompleteMfaAsync(string mfaCode, CancellationToken cancellationToken)
    {
        var url = BuildUrl(MfaCodeUrl, EmbedQuery());
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        AddSsoHeaders(request);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["embed"] = "true",
            ["fromPage"] = "setupEnterMfaCode",
            ["_csrf"] = _csrf ?? string.Empty,
            ["mfa-code"] = mfaCode,
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is not null)
            return await HandleRedirectAsync(response.Headers.Location.ToString(), cancellationToken, 0);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body == "error code: 1020")
            throw new GarminConnectAuthenticationException("MFA blocked by Cloudflare.")
            { Code = Code.MfaBlockedCloudflare };
        throw new GarminConnectAuthenticationException("MFA code rejected by Garmin.")
        { Code = Code.MfaInvalidCode };
    }

    private async Task<string> HandleRedirectAsync(string location, CancellationToken cancellationToken, uint depth)
    {
        if (depth >= 3)
            return string.Empty;
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        AddSsoHeaders(request);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is not null)
            return await HandleRedirectAsync(response.Headers.Location.ToString(), cancellationToken, depth + 1);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<OAuth1Token> GetOAuth1TokenAsync(string ticket, CancellationToken cancellationToken)
    {
        var url = $"https://connectapi.{_domain}/oauth-service/oauth/preauthorized" +
                  $"?ticket={ticket}&login-url=https://sso.garmin.com/sso/embed&accepts-mfa-tokens=true";
        var auth = OAuth1Signer.BuildAuthorizationHeader(
            "GET", url, _consumer.ConsumerKey, _consumer.ConsumerSecret);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Authorization", auth);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            throw new GarminConnectAuthenticationException("OAuth1 token response was empty.")
            { Code = Code.OAuth1TokenNotFound };

        var parsed = ParseFormEncoded(body);
        if (!parsed.TryGetValue("oauth_token", out var token) || string.IsNullOrWhiteSpace(token))
            throw new GarminConnectAuthenticationException("OAuth1 token missing in response: " + body)
            { Code = Code.OAuth1TokenNotFound };
        if (!parsed.TryGetValue("oauth_token_secret", out var secret) || string.IsNullOrWhiteSpace(secret))
            throw new GarminConnectAuthenticationException("OAuth1 token secret missing in response: " + body)
            { Code = Code.OAuth1TokenNotFound };

        return new OAuth1Token { Token = token, TokenSecret = secret, Domain = _domain };
    }

    private void AddSsoHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("origin", $"https://sso.{_domain}");
        if (!string.IsNullOrEmpty(_cookies))
            request.Headers.TryAddWithoutValidation("cookie", _cookies);
    }

    private static string FindCsrf(string body, Code failureCode)
    {
        var match = CsrfRegex.Match(body ?? string.Empty);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["csrf"].Value))
            throw new GarminConnectAuthenticationException("Failed to find CSRF token in Garmin's response.")
            { Code = failureCode };
        return match.Groups["csrf"].Value;
    }

    private static readonly KeyValuePair<string, string>[] BaseQuery =
    {
        new("id", "gauth-widget"),
        new("embedWidget", "true"),
    };

    private IEnumerable<KeyValuePair<string, string>> EmbedQuery() => MergeQuery(
        BaseQuery,
        ("gauthHost", EmbedUrl),
        ("service", EmbedUrl),
        ("source", EmbedUrl),
        ("redirectAfterAccountLoginUrl", EmbedUrl),
        ("redirectAfterAccountCreationUrl", EmbedUrl));

    private static IEnumerable<KeyValuePair<string, string>> MergeQuery(
        IEnumerable<KeyValuePair<string, string>> baseQuery, params (string Key, string Value)[] extra)
    {
        foreach (var kv in baseQuery)
            yield return kv;
        foreach (var (key, value) in extra)
            yield return new(key, value);
    }

    private static string BuildUrl(string baseUrl, IEnumerable<KeyValuePair<string, string>> query) =>
        baseUrl + "?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static Dictionary<string, string> ParseFormEncoded(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
                result[Uri.UnescapeDataString(part)] = string.Empty;
            else
                result[Uri.UnescapeDataString(part[..idx])] = Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return result;
    }
}

/// <summary>Result of a full SSO login: the durable OAuth1 token + an initial OAuth2 token.</summary>
public sealed record GarminAuthResult(OAuth1Token Oauth1, OAuth2Token Oauth2);
