using Garmin.Connect;
using GarminMcp.Core.Auth;
using GarminMcp.Core.Metrics;

namespace GarminMcp.Core;

public enum LoginStatus
{
    Success,
    MfaRequired,
    Error,
}

public sealed record LoginOutcome(LoginStatus Status, string? Message = null)
{
    public static LoginOutcome Success() => new(LoginStatus.Success);
    public static LoginOutcome MfaRequired() => new(LoginStatus.MfaRequired);
    public static LoginOutcome Error(string message) => new(LoginStatus.Error, message);
}

/// <summary>
/// Holds the current Garmin connection and lets it be (re)established at runtime via a
/// browser-based login. Tools use <see cref="Service"/>; when not signed in, calls throw
/// <see cref="GarminNotAuthenticatedException"/> carrying the setup URL.
/// </summary>
public interface IGarminConnectionProvider
{
    bool IsAuthenticated { get; }
    string SetupUrl { get; }
    IGarminService Service { get; }

    /// <summary>Raw metrics client (training readiness/status/race) — null until signed in.</summary>
    GarminMetricsClient? Metrics { get; }

    /// <summary>Attempt to connect from configured token/credentials. Never throws.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Start a browser login. Returns Success, MfaRequired (then call <see cref="SubmitMfaAsync"/>), or Error.</summary>
    Task<LoginOutcome> BeginLoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Provide the MFA code for a login that returned MfaRequired.</summary>
    Task<LoginOutcome> SubmitMfaAsync(string code, CancellationToken cancellationToken = default);
}

public sealed class GarminConnectionProvider : IGarminConnectionProvider
{
    private readonly HttpClient _http;
    private readonly GarminOptions _options;
    private readonly Action<string>? _warn;
    private readonly GarminService _service;
    private readonly object _gate = new();

    private IGarminConnectClient? _client;
    private GarminMetricsClient? _metrics;
    private Session? _session;

    public GarminConnectionProvider(HttpClient http, GarminOptions options, string setupUrl, Action<string>? warn = null)
    {
        _http = http;
        _options = options;
        SetupUrl = setupUrl;
        _warn = warn;
        _service = new GarminService(RequireClient);
    }

    public string SetupUrl { get; }

    public bool IsAuthenticated => _client is not null;

    public IGarminService Service => _service;

    public GarminMetricsClient? Metrics => _metrics;

    private IGarminConnectClient RequireClient() =>
        _client ?? throw new GarminNotAuthenticatedException(SetupUrl);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.HasToken && string.IsNullOrWhiteSpace(_options.TokenFile) && !_options.HasCredentials)
        {
            _warn?.Invoke($"No Garmin credentials configured; sign in at {SetupUrl}");
            return;
        }

        try
        {
            var result = await GarminConnection.ResolveAsync(_options, _http, cancellationToken);
            _client = result.Client;
            _metrics = new GarminMetricsClient(result.Context);
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Garmin auto-login failed ({ex.Message}); sign in at {SetupUrl}");
        }
    }

    public async Task<LoginOutcome> BeginLoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return LoginOutcome.Error("Email and password are required.");

        var mfa = new WebMfaCodeProvider();
        var sso = new GarminSsoClient(_http, _options.Domain);
        var loginTask = sso.LoginAsync(email, password, mfa, cancellationToken);

        lock (_gate)
        {
            _session?.Mfa.Cancel();
            _session = new Session(loginTask, mfa);
        }

        var first = await Task.WhenAny(loginTask, mfa.Requested);
        if (first == loginTask || loginTask.IsCompleted)
            return await CompleteAsync(loginTask);

        return LoginOutcome.MfaRequired();
    }

    public async Task<LoginOutcome> SubmitMfaAsync(string code, CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
            return LoginOutcome.Error("No login is awaiting an MFA code. Start the login again.");
        if (string.IsNullOrWhiteSpace(code))
            return LoginOutcome.Error("MFA code is required.");

        session.Mfa.Provide(code.Trim());
        return await CompleteAsync(session.LoginTask);
    }

    private async Task<LoginOutcome> CompleteAsync(Task<GarminAuthResult> loginTask)
    {
        try
        {
            var result = await loginTask;
            var bundle = new GarminTokenBundle { Oauth1 = result.Oauth1, Oauth2 = result.Oauth2 };
            var context = GarminClientFactory.CreateContextFromToken(bundle, _http);
            _client = GarminClientFactory.CreateClient(context);
            _metrics = new GarminMetricsClient(context);
            ClearSession();
            await PersistAsync(bundle);
            return LoginOutcome.Success();
        }
        catch (OperationCanceledException)
        {
            ClearSession();
            return LoginOutcome.Error("Login was cancelled.");
        }
        catch (Exception ex)
        {
            ClearSession();
            return LoginOutcome.Error(ex.Message);
        }
    }

    private async Task PersistAsync(GarminTokenBundle bundle)
    {
        if (string.IsNullOrWhiteSpace(_options.TokenFile))
            return;
        try
        {
            await File.WriteAllTextAsync(_options.TokenFile, bundle.ToJson());
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Could not persist token to {_options.TokenFile}: {ex.Message}");
        }
    }

    private void ClearSession()
    {
        lock (_gate)
        {
            _session = null;
        }
    }

    private sealed record Session(Task<GarminAuthResult> LoginTask, WebMfaCodeProvider Mfa);
}
