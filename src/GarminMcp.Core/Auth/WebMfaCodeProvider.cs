using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>
/// An <see cref="IMfaCodeProvider"/> for a web/async login flow: when the SSO flow
/// needs the MFA code it signals <see cref="Requested"/> and then awaits a code that
/// the web UI supplies later via <see cref="Provide"/>.
/// </summary>
public sealed class WebMfaCodeProvider : IMfaCodeProvider
{
    private readonly TaskCompletionSource<string> _code = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the SSO flow has asked for an MFA code.</summary>
    public Task Requested => _requested.Task;

    /// <summary>True once an MFA code has been requested by the login flow.</summary>
    public bool IsRequested => _requested.Task.IsCompleted;

    public Task<string> GetMfaCodeAsync()
    {
        _requested.TrySetResult();
        return _code.Task;
    }

    /// <summary>Supply the MFA code the user entered in the web UI.</summary>
    public void Provide(string code) => _code.TrySetResult(code);

    /// <summary>Abort a pending MFA wait (e.g. on cancel/restart).</summary>
    public void Cancel() => _code.TrySetCanceled();
}
