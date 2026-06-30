using System.Security.Cryptography;
using System.Text;

namespace GarminMcp.Core.Auth;

/// <summary>
/// Minimal, standards-correct OAuth 1.0a HMAC-SHA1 request signer.
/// Produces the value for the HTTP <c>Authorization</c> header.
///
/// This reproduces the OAuth1 signing that Unofficial.Garmin.Connect performs
/// internally (it bundles Daniel Crenna's OAuth library), so that we can drive
/// the Garmin token exchange ourselves and persist the long-lived OAuth1 token
/// for token-first / password-free operation.
///
/// Verified against the canonical Twitter OAuth example (see OAuth1SignerTests).
/// </summary>
public static class OAuth1Signer
{
    private const string Unreserved =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>RFC 3986 percent-encoding (uppercase hex), as required by OAuth.</summary>
    public static string Encode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (Unreserved.IndexOf(c) >= 0)
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the full <c>Authorization: OAuth ...</c> header value for a request.
    /// Query-string parameters present in <paramref name="url"/> are folded into the
    /// signature base string (required by OAuth); <paramref name="extraParameters"/>
    /// carries x-www-form-urlencoded body parameters that must also be signed.
    /// </summary>
    public static string BuildAuthorizationHeader(
        string method,
        string url,
        string consumerKey,
        string consumerSecret,
        string? token = null,
        string? tokenSecret = null,
        IEnumerable<KeyValuePair<string, string>>? extraParameters = null,
        string? timestamp = null,
        string? nonce = null)
    {
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        nonce ??= GenerateNonce();

        var uri = new Uri(url);
        var baseUrl =
            $"{uri.Scheme}://{uri.Host}{(IsDefaultPort(uri) ? string.Empty : $":{uri.Port}")}{uri.AbsolutePath}";

        var oauthParams = new List<KeyValuePair<string, string>>
        {
            new("oauth_consumer_key", consumerKey),
            new("oauth_nonce", nonce),
            new("oauth_signature_method", "HMAC-SHA1"),
            new("oauth_timestamp", timestamp),
            new("oauth_version", "1.0"),
        };
        if (!string.IsNullOrEmpty(token))
            oauthParams.Add(new("oauth_token", token!));

        // Signature base string: oauth params + URL query params + body params.
        var signatureParams = new List<KeyValuePair<string, string>>(oauthParams);
        signatureParams.AddRange(ParseQuery(uri.Query));
        if (extraParameters is not null)
            signatureParams.AddRange(extraParameters);

        var normalized = string.Join("&", signatureParams
            .Select(p => new KeyValuePair<string, string>(Encode(p.Key), Encode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

        var signatureBase = $"{method.ToUpperInvariant()}&{Encode(baseUrl)}&{Encode(normalized)}";
        var signingKey = $"{Encode(consumerSecret)}&{Encode(tokenSecret ?? string.Empty)}";

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(signingKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureBase)));

        var headerParams = new List<KeyValuePair<string, string>>(oauthParams)
        {
            new("oauth_signature", signature),
        };

        return "OAuth " + string.Join(", ", headerParams
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Encode(p.Key)}=\"{Encode(p.Value)}\""));
    }

    private static bool IsDefaultPort(Uri uri) =>
        (uri.Scheme == "http" && uri.Port == 80) || (uri.Scheme == "https" && uri.Port == 443);

    private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
            yield break;
        if (query[0] == '?')
            query = query[1..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
                yield return new(Uri.UnescapeDataString(part), string.Empty);
            else
                yield return new(
                    Uri.UnescapeDataString(part[..idx]),
                    Uri.UnescapeDataString(part[(idx + 1)..]));
        }
    }

    private static string GenerateNonce()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz1234567890";
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder(16);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }
}
