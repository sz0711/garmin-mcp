using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>Supplies an MFA code from a delegate (e.g. console input, a TOTP generator).</summary>
public sealed class DelegateMfaCodeProvider : IMfaCodeProvider
{
    private readonly Func<Task<string>> _provider;
    public DelegateMfaCodeProvider(Func<Task<string>> provider) => _provider = provider;
    public DelegateMfaCodeProvider(Func<string> provider) => _provider = () => Task.FromResult(provider());
    public Task<string> GetMfaCodeAsync() => _provider();
}

/// <summary>Supplies a fixed, pre-known MFA code (rarely useful — codes are time-based).</summary>
public sealed class StaticMfaCodeProvider : IMfaCodeProvider
{
    private readonly string _code;
    public StaticMfaCodeProvider(string code) => _code = code;
    public Task<string> GetMfaCodeAsync() => Task.FromResult(_code);
}
