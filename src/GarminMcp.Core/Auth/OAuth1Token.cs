using System.Text.Json.Serialization;

namespace GarminMcp.Core.Auth;

/// <summary>
/// The long-lived (~1 year) Garmin OAuth1 token + secret. This is the durable
/// credential: from it we can mint short-lived OAuth2 access tokens forever
/// (until it expires/revokes) WITHOUT the password or MFA. It is what we persist
/// and ship to the running container as the <c>GARMIN_TOKEN</c>.
/// </summary>
public sealed record OAuth1Token
{
    [JsonPropertyName("oauth_token")]
    public required string Token { get; init; }

    [JsonPropertyName("oauth_token_secret")]
    public required string TokenSecret { get; init; }

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "garmin.com";
}
