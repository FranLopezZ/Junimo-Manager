using System.Net;
using System.Text;
using SdvMods.Core.Game;
using SdvMods.Core.Settings;
using SdvMods.Core.Sync;
using SdvMods.Core.Updates;
using Xunit.Abstractions;

namespace SdvMods.Tests;

sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    public List<string> Bodies { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
    }
}

public class SecretsTests
{
    [Fact]
    public void RoundTripsAndNeverStoresPlainText()
    {
        var stored = Secrets.Protect("super-secret-token");
        Assert.NotNull(stored);
        Assert.DoesNotContain("super-secret-token", stored);
        Assert.Equal("super-secret-token", Secrets.Unprotect(stored));
        Assert.Null(Secrets.Protect(""));
        Assert.Null(Secrets.Unprotect("dpapi:not-valid-base64!!"));
    }

    [Fact]
    public void SettingsFileContainsNoPlainKeys()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { NexusApiKey = "NEXUS-KEY-123", GitHubToken = "ghp_abc" });
        Assert.DoesNotContain("NEXUS-KEY-123", json);
        Assert.DoesNotContain("ghp_abc", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("NEXUS-KEY-123", back.NexusApiKey);
        Assert.Equal("ghp_abc", back.GitHubToken);
    }
}

public class DeviceFlowTests
{
    [Fact]
    public async Task WaitsWhilePendingThenReturnsToken()
    {
        var polls = 0;
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/device/code"))
                return "{\"device_code\":\"dc\",\"user_code\":\"ABCD-1234\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":1}";
            return ++polls < 2 ? "{\"error\":\"authorization_pending\"}" : "{\"access_token\":\"gho_token\",\"token_type\":\"bearer\"}";
        });
        var flow = new GitHubDeviceFlow(new HttpClient(handler), "client123");
        var code = await flow.StartAsync();
        Assert.Equal("ABCD-1234", code.UserCode);
        Assert.Equal("gho_token", await flow.WaitForTokenAsync(code));
        Assert.Contains("client_id=client123", handler.Bodies[0]);
        Assert.Contains("scope=gist", handler.Bodies[0]);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", handler.Bodies[1]);
    }

    [Fact]
    public async Task DeniedAccessThrows()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/device/code")
            ? "{\"device_code\":\"dc\",\"user_code\":\"X\",\"verification_uri\":\"u\",\"expires_in\":900,\"interval\":1}"
            : "{\"error\":\"access_denied\"}");
        var flow = new GitHubDeviceFlow(new HttpClient(handler), "c");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await flow.WaitForTokenAsync(await flow.StartAsync()));
    }
}

public class SmapiInstallerLiveTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task DownloadsOfficialInstallerPackage()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        using var t = new TempDir();
        using var http = Http.Create();
        var installer = new SmapiInstaller(http);
        var rel = await installer.LatestAsync();
        Assert.NotNull(rel);
        var dir = await installer.PrepareAsync(rel!, t.Path);
        output.WriteLine($"SMAPI {rel!.Version} -> {dir}");
        Assert.True(File.Exists(Path.Combine(dir, "internal", "windows", "SMAPI.Installer.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "internal", "linux", "SMAPI.Installer")));
    }
}
