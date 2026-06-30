using Garmin.Connect.Auth;

namespace GarminMcp.Core.Auth;

/// <summary>
/// An <see cref="ITokenCache"/> that delivers the durable token-first behaviour the
/// library lacks: it keeps a short-lived OAuth2 token in memory and, when it is
/// missing or about to expire, mints a new one FROM THE OAUTH1 TOKEN via
/// <see cref="GarminSsoClient.ExchangeOAuth2Async"/> — no password, no MFA.
///
/// Because it never returns null/expired, Unofficial.Garmin.Connect never triggers
/// its own password-based re-login.
/// </summary>
public sealed class RefreshingTokenCache : ITokenCache
{
    private readonly OAuth1Token _oauth1;
    private readonly GarminSsoClient _sso;
    private readonly TimeSpan _refreshSkew;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private OAuth2Token? _token;
    private DateTimeOffset _expiresAt;

    public RefreshingTokenCache(OAuth1Token oauth1, GarminSsoClient sso,
        OAuth2Token? initial = null, TimeSpan? refreshSkew = null)
    {
        _oauth1 = oauth1;
        _sso = sso;
        _refreshSkew = refreshSkew ?? TimeSpan.FromMinutes(5);
        if (initial is not null)
            Store(initial);
    }

    public async Task<OAuth2Token> GetOAuth2Token(CancellationToken cancellationToken)
    {
        if (IsFresh())
            return _token!;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh())
                return _token!;
            var fresh = await _sso.ExchangeOAuth2Async(_oauth1, cancellationToken);
            Store(fresh);
            return fresh;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task SetOAuth2Token(OAuth2Token token, CancellationToken cancellationToken)
    {
        Store(token);
        return Task.CompletedTask;
    }

    private bool IsFresh() => _token is not null && DateTimeOffset.UtcNow < _expiresAt - _refreshSkew;

    private void Store(OAuth2Token token)
    {
        _token = token;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
    }
}
