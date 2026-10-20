using System.Net;
using System.Text;
using SdvMods.Core.Sync;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

/// <summary>Answers with a status code and body chosen per request, and remembers the requests.</summary>
sealed class RouteHandler(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<string> Bodies { get; } = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body); Requests.Add(request);
        var (status, text) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public class GitHubSessionTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    const string Fresh = """{"access_token":"gho_NEW","refresh_token":"ghr_NEW","expires_in":28800,"refresh_token_expires_in":15897600,"token_type":"bearer"}""";

    [Fact]
    public async Task SignInKeepsTheRefreshTokenAndTheExpiry()
    {
        var handler = new RouteHandler((req, _) => (HttpStatusCode.OK, req.RequestUri!.AbsolutePath.EndsWith("/device/code")
            ? """{"device_code":"dc","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":1}"""
            : Fresh));
        var flow = new GitHubDeviceFlow(new HttpClient(handler), "client123");
        var tokens = await flow.WaitForTokensAsync(await flow.StartAsync());
        Assert.Equal("gho_NEW", tokens.AccessToken);
        Assert.Equal("ghr_NEW", tokens.RefreshToken);
        Assert.InRange((tokens.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalHours, 7.9, 8.1);
    }

    [Fact]
    public async Task ATokenThatDoesNotExpireIsUsedAsItIsAndNeverRefreshed()
    {
        var handler = new RouteHandler((_, _) => (HttpStatusCode.OK, Fresh));
        var session = new GitHubSession(new HttpClient(handler), "client123", "gho_OLD", null, null, () => Now);
        Assert.Equal("gho_OLD", await session.GetValidTokenAsync());
        Assert.False(await session.TryRefreshAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnExpiredTokenIsRenewedBeforeUseAndTheNewOnesAreSaved()
    {
        var handler = new RouteHandler((_, _) => (HttpStatusCode.OK, Fresh));
        var session = new GitHubSession(new HttpClient(handler), "client123", "gho_OLD", "ghr_OLD", Now.AddHours(-1), () => Now);
        var changed = 0;
        session.Changed += () => changed++;

        Assert.Equal("gho_NEW", await session.GetValidTokenAsync());
        Assert.Equal("ghr_NEW", session.RefreshToken);                      // the old refresh token is single-use, the new one replaces it
        Assert.Equal(1, changed);
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=ghr_OLD", body);
        Assert.Contains("client_id=client123", body);
        Assert.DoesNotContain("client_secret", body);                       // device-flow tokens refresh without a secret

        Assert.Equal("gho_NEW", await session.GetValidTokenAsync());        // fresh now: no second request
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ATokenAboutToExpireIsRenewedEarly()
    {
        var handler = new RouteHandler((_, _) => (HttpStatusCode.OK, Fresh));
        var session = new GitHubSession(new HttpClient(handler), "c", "gho_OLD", "ghr_OLD", Now.AddMinutes(3), () => Now);
        Assert.True(session.NeedsRefresh);
        Assert.False(session.IsExpired);
        Assert.Equal("gho_NEW", await session.GetValidTokenAsync());
    }

    [Fact]
    public async Task ARevokedRefreshTokenEndsTheSession()
    {
        var handler = new RouteHandler((_, _) => (HttpStatusCode.OK, """{"error":"bad_refresh_token","error_description":"The refresh token passed is incorrect or expired."}"""));
        var session = new GitHubSession(new HttpClient(handler), "c", "gho_OLD", "ghr_OLD", Now.AddHours(-1), () => Now);
        Assert.Null(await session.GetValidTokenAsync());                   // never hand out a token GitHub would reject
        Assert.False(session.IsLoggedIn);
        Assert.Null(session.RefreshToken);
    }

    [Fact]
    public async Task BeingOfflineDoesNotLogYouOut()
    {
        var handler = new RouteHandler((_, _) => throw new HttpRequestException("no network"));
        var soon = new GitHubSession(new HttpClient(handler), "c", "gho_OLD", "ghr_OLD", Now.AddMinutes(2), () => Now);
        Assert.Equal("gho_OLD", await soon.GetValidTokenAsync());          // still valid for two minutes: use it
        Assert.True(soon.IsLoggedIn);

        var expired = new GitHubSession(new HttpClient(handler), "c", "gho_OLD", "ghr_OLD", Now.AddHours(-1), () => Now);
        Assert.Null(await expired.GetValidTokenAsync());                   // cannot be used, but the login is kept for the next try
        Assert.True(expired.IsLoggedIn);
        Assert.Equal("ghr_OLD", expired.RefreshToken);
    }

    [Fact]
    public async Task APublicReleaseLookupStillWorksWhenTheSavedTokenWasRejected()
    {
        const string releases = """
            [{"draft":false,"prerelease":false,"tag_name":"v1.2.0","assets":[{"name":"MyMod 1.2.0.zip","browser_download_url":"https://example/MyMod-1.2.0.zip"}]}]
            """;
        var handler = new RouteHandler((req, _) => req.Headers.Authorization is not null
            ? (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""")
            : (HttpStatusCode.OK, releases));
        var asset = await new GitHubReleases(new HttpClient(handler), "gho_EXPIRED").FindAsync("someone/MyMod", "1.2.0", "MyMod", "someone.MyMod");
        Assert.NotNull(asset);
        Assert.Equal(2, handler.Requests.Count);                           // first with the token (401), then without it
        Assert.Null(handler.Requests[1].Headers.Authorization);
    }
}
