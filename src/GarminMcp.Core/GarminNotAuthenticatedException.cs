namespace GarminMcp.Core;

/// <summary>
/// Thrown when a Garmin operation is attempted but the service is not signed in.
/// Carries the setup URL the user should open in a browser to sign in.
/// </summary>
public sealed class GarminNotAuthenticatedException : Exception
{
    public string SetupUrl { get; }

    public GarminNotAuthenticatedException(string setupUrl)
        : base($"Not signed in to Garmin. Open {setupUrl} in your browser, sign in and save, then try again.")
    {
        SetupUrl = setupUrl;
    }
}
