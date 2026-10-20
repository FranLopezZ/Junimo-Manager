namespace SdvMods.Core.Sync;

/// <summary>
/// The GitHub login of this device. OAuth apps can be set to issue tokens that expire (8 hours) together with a refresh token
/// (6 months), so the session renews the access token by itself before it expires or when GitHub answers 401, and only asks for a new
/// sign-in when the refresh token is gone too. Tokens without an expiry (older tokens, or one pasted by hand) are used as they are.
/// </summary>
public sealed class GitHubSession
{
    static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    readonly HttpClient _http;
    readonly string _clientId;
    readonly Func<DateTimeOffset> _now;
    readonly SemaphoreSlim _refreshing = new(1, 1);

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Raised whenever the stored tokens change, so the owner can save them.</summary>
    public event Action? Changed;

    public GitHubSession(HttpClient http, string clientId, string? accessToken, string? refreshToken, DateTimeOffset? expiresAt, Func<DateTimeOffset>? now = null)
    {
        _http = http; _clientId = clientId; _now = now ?? (() => DateTimeOffset.UtcNow);
        AccessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken;
        RefreshToken = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken;
        ExpiresAt = expiresAt;
    }

    public bool IsLoggedIn => AccessToken is not null;
    /// <summary>The access token is expired (or about to be).</summary>
    public bool NeedsRefresh => ExpiresAt is { } e && _now() >= e - Margin;
    /// <summary>The access token is already past its expiry, so GitHub would reject it.</summary>
    public bool IsExpired => ExpiresAt is { } e && _now() >= e;

    public void SetTokens(GitHubTokens? tokens)
    {
        AccessToken = tokens?.AccessToken; RefreshToken = tokens?.RefreshToken; ExpiresAt = tokens?.ExpiresAt;
        Changed?.Invoke();
    }

    public void Clear() => SetTokens(null);

    /// <summary>
    /// A token that GitHub should accept: renewed first when it is (nearly) expired. Null when there is no usable login
    /// (not signed in, or expired with no way to renew it).
    /// </summary>
    public async Task<string?> GetValidTokenAsync(CancellationToken ct = default)
    {
        if (AccessToken is null) return null;
        if (!NeedsRefresh) return AccessToken;
        if (await TryRefreshAsync(ct)) return AccessToken;
        return IsExpired ? null : AccessToken;
    }

    /// <summary>
    /// Renews the access token with the refresh token. False if there is no refresh token or it could not be renewed. When GitHub
    /// says the refresh token itself is no longer valid, the login is cleared (the user must sign in again).
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            var refresh = RefreshToken;
            if (refresh is null) return false;
            try
            {
                var tokens = await new GitHubDeviceFlow(_http, _clientId).RefreshAsync(refresh, ct);
                SetTokens(tokens);
                return true;
            }
            catch (GitHubSessionExpiredException)
            {
                Clear();
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return false; // offline or GitHub unreachable: keep the login and try again later
            }
        }
        finally { _refreshing.Release(); }
    }
}
