namespace GarminMcp.Core;

/// <summary>
/// Configuration for connecting to Garmin Connect. Bound from configuration /
/// environment variables (prefix <c>GARMIN_</c>). Token-first is the intended mode:
/// set <see cref="Token"/> only.
/// </summary>
public sealed class GarminOptions
{
    public const string SectionName = "Garmin";

    /// <summary>
    /// The credential bundle (base64 or JSON) produced by the login tool — the
    /// long-lived OAuth1 token. This is the recommended, password-free input.
    /// Env: <c>GARMIN_TOKEN</c>.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Email — only needed to mint a token (fallback / first run). Env: <c>GARMIN_EMAIL</c>.</summary>
    public string? Email { get; set; }

    /// <summary>Password — only needed to mint a token (fallback / first run). Env: <c>GARMIN_PASSWORD</c>.</summary>
    public string? Password { get; set; }

    /// <summary>An MFA code, if minting a token non-interactively at startup. Env: <c>GARMIN_MFA_CODE</c>.</summary>
    public string? MfaCode { get; set; }

    /// <summary>Garmin domain: <c>garmin.com</c> (default) or <c>garmin.cn</c> (China). Env: <c>GARMIN_DOMAIN</c>.</summary>
    public string Domain { get; set; } = "garmin.com";

    /// <summary>
    /// Optional path to persist/refresh the token bundle (e.g. a Docker volume), so a
    /// password login only happens once. Env: <c>GARMIN_TOKEN_FILE</c>.
    /// </summary>
    public string? TokenFile { get; set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}
