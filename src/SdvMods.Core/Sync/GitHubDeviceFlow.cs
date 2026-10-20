using SdvMods.Core.Localization;
using System.Text.Json;

namespace SdvMods.Core.Sync;

public sealed record DeviceCode(string DeviceCodeValue, string UserCode, string VerificationUri, int IntervalSeconds, int ExpiresInSeconds);

/// <summary>An access token and, when the OAuth app issues expiring tokens, the refresh token and the moment the access token stops working.</summary>
public sealed record GitHubTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

/// <summary>The refresh token is invalid, expired or revoked: the user has to sign in again.</summary>
public sealed class GitHubSessionExpiredException : InvalidOperationException
{
    public GitHubSessionExpiredException(string message) : base(message) { }
}

/// <summary>
/// GitHub "device flow" sign-in: the user opens a page, types a short code and approves; no token is ever pasted by hand.
/// It only needs the public Client ID of an OAuth App registered by the tool's author (no secret).
/// </summary>
public sealed class GitHubDeviceFlow
{
    readonly HttpClient _http;
    readonly string _clientId;

    public GitHubDeviceFlow(HttpClient http, string clientId) { _http = http; _clientId = clientId; }

    async Task<JsonElement> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException(Loc.F("Respuesta inesperada de GitHub ({0}).", (int)resp.StatusCode)); }
    }

    public async Task<DeviceCode> StartAsync(CancellationToken ct = default)
    {
        var j = await PostAsync("https://github.com/login/device/code", new() { ["client_id"] = _clientId, ["scope"] = "gist" }, ct);
        if (j.TryGetProperty("error", out var err))
            throw new InvalidOperationException(Loc.T("GitHub rechazó el inicio de sesión: ") + (j.TryGetProperty("error_description", out var d) ? d.GetString() : err.GetString()));
        return new DeviceCode(
            j.GetProperty("device_code").GetString()!, j.GetProperty("user_code").GetString()!,
            j.GetProperty("verification_uri").GetString()!, j.GetProperty("interval").GetInt32(), j.GetProperty("expires_in").GetInt32());
    }

    static GitHubTokens ReadTokens(JsonElement j, string accessToken)
    {
        var refresh = j.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        DateTimeOffset? expires = j.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UtcNow.AddSeconds(e.GetInt64()) : null;
        return new GitHubTokens(accessToken, string.IsNullOrEmpty(refresh) ? null : refresh, expires);
    }

    /// <summary>Gets a new access token (and a new refresh token) with a refresh token. A device-flow token needs no client secret.</summary>
    public async Task<GitHubTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var j = await PostAsync("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = _clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
        }, ct);
        if (j.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } token) return ReadTokens(j, token);
        var error = j.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (error is "bad_refresh_token" or "incorrect_client_credentials" or "unauthorized_client")
            throw new GitHubSessionExpiredException(Loc.T("La sesión de GitHub caducó o fue revocada. Vuelve a iniciar sesión en Ajustes."));
        throw new InvalidOperationException(Loc.T("Error de GitHub: ") + (error ?? "?"));
    }

    /// <summary>Waits until the user approves in the browser. Returns the access token only (see <see cref="WaitForTokensAsync"/>).</summary>
    public async Task<string> WaitForTokenAsync(DeviceCode code, CancellationToken ct = default)
        => (await WaitForTokensAsync(code, ct)).AccessToken;

    /// <summary>Waits until the user approves in the browser. Returns the access token with its refresh token and expiry, if GitHub gave them.</summary>
    public async Task<GitHubTokens> WaitForTokensAsync(DeviceCode code, CancellationToken ct = default)
    {
        var interval = Math.Max(code.IntervalSeconds, 1);
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresInSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var j = await PostAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = _clientId,
                ["device_code"] = code.DeviceCodeValue,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, ct);
            if (j.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } token) return ReadTokens(j, token);
            var error = j.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending": break;
                case "slow_down": interval += 5; break;
                case "access_denied": throw new InvalidOperationException(Loc.T("Cancelaste el acceso en GitHub."));
                case "expired_token": throw new InvalidOperationException(Loc.T("El código caducó; vuelve a intentarlo."));
                default: throw new InvalidOperationException(Loc.T("Error de GitHub: ") + (error ?? "desconocido"));
            }
        }
        throw new TimeoutException(Loc.T("El código caducó; vuelve a intentarlo."));
    }
}
