namespace GarminMcp.Core;

/// <summary>A clean, user-facing error from the Garmin service layer.</summary>
public sealed class GarminServiceException : Exception
{
    public GarminServiceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
