using System.Text.Json;
using System.Text.Json.Serialization;
using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>
/// The persisted credential bundle = what the user sets as the <c>GARMIN_TOKEN</c>
/// environment variable in the MCP / Docker configuration. Contains the long-lived
/// OAuth1 token (mandatory) and optionally the last OAuth2 token snapshot (a warm
/// start; it is refreshed automatically anyway).
/// </summary>
public sealed record GarminTokenBundle
{
    [JsonPropertyName("oauth1")]
    public required OAuth1Token Oauth1 { get; init; }

    [JsonPropertyName("oauth2")]
    public OAuth2Token? Oauth2 { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Compact JSON (for a token file or inspection).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Base64 of the JSON — the single-string form for the env var.</summary>
    public string ToBase64() =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    public static GarminTokenBundle FromJson(string json) =>
        JsonSerializer.Deserialize<GarminTokenBundle>(json, JsonOptions)
        ?? throw new FormatException("GARMIN_TOKEN JSON deserialized to null.");

    /// <summary>
    /// Accepts either the base64 form or raw JSON (auto-detected), so users can paste
    /// whichever they have.
    /// </summary>
    public static GarminTokenBundle Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("GARMIN_TOKEN is empty.");

        var trimmed = value.Trim();
        if (trimmed.StartsWith('{'))
            return FromJson(trimmed);

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            return FromJson(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new FormatException(
                "GARMIN_TOKEN is neither valid base64 nor JSON. Re-create it with the login tool.", ex);
        }
    }
}
