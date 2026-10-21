using System.Net;
using System.Text.Json;
using SdvMods.App.ViewModels;
using SdvMods.Core;
using SdvMods.Core.Mods;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

/// <summary>The edition hosted on Nexus Mods never downloads anything from outside Nexus.</summary>
[Collection("ViewModel")]   // Edition is static: keep these away from the tests that run in parallel
public sealed class NexusEditionTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR");

    public NexusEditionTests() => Edition.IsNexus = true;

    public void Dispose()
    {
        Edition.IsNexus = false;
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", _oldConfig);
        _dir.Dispose();
    }

    const string SmapiAnswer = """
        [{"id":"someone.MyMod","suggestedUpdate":{"version":"2.0.0","url":"https://www.nexusmods.com/stardewvalley/mods/123"},
          "metadata":{"name":"My Mod","nexusID":123,"gitHubRepo":"someone/MyMod"}}]
        """;

    static InstalledMod Installed() => new(new ModManifest
    {
        UniqueID = "someone.MyMod", Name = "My Mod", Version = "1.0.0", UpdateKeys = { "Nexus:123", "GitHub:someone/MyMod" },
    }, "/mods/MyMod", true);

    static RouteHandler Handler() => new((req, _) => req.RequestUri!.Host == "smapi.io"
        ? (HttpStatusCode.OK, SmapiAnswer)
        : (HttpStatusCode.NotFound, "{}"));

    [Fact]
    public async Task ModsThatAlsoPublishOnGitHubAreUpdatedThroughNexusOnly()
    {
        var handler = Handler();
        var found = await new UpdateService(new HttpClient(handler)).CheckAsync(new[] { Installed() });

        var update = Assert.Single(found);
        Assert.Equal(UpdateMethod.NexusBrowser, update.Method);          // the user clicks "Mod Manager Download" on Nexus
        Assert.Null(update.DownloadUrl);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host.Contains("github"));
    }

    [Fact]
    public async Task TheRegularEditionStillLooksOnGitHub()
    {
        Edition.IsNexus = false;
        var handler = Handler();
        await new UpdateService(new HttpClient(handler)).CheckAsync(new[] { Installed() });
        Assert.Contains(handler.Requests, r => r.RequestUri!.Host == "api.github.com");   // proves the switch is what changes the behaviour
    }

    [Fact]
    public async Task ModsOnlyOnGitHubAreNotFetchedEitherJustPointedTo()
    {
        var handler = new RouteHandler((req, _) => req.RequestUri!.Host == "smapi.io"
            ? (HttpStatusCode.OK, """[{"id":"x.Only","suggestedUpdate":{"version":"2.0.0","url":"https://github.com/x/Only/releases"},"metadata":{"name":"Only","gitHubRepo":"x/Only"}}]""")
            : (HttpStatusCode.NotFound, "{}"));
        var mod = new InstalledMod(new ModManifest { UniqueID = "x.Only", Name = "Only", Version = "1.0.0", UpdateKeys = { "GitHub:x/Only" } }, "/m/Only", true);
        var update = Assert.Single(await new UpdateService(new HttpClient(handler)).CheckAsync(new[] { mod }));
        Assert.Equal(UpdateMethod.Manual, update.Method);                 // "open page": the user downloads it themselves
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host.Contains("github"));
    }

    [Fact]
    public async Task TheProgramDoesNotLookForOrDownloadItsOwnUpdates()
    {
        var handler = new RouteHandler((_, _) => throw new InvalidOperationException("no request is expected"));
        Assert.Null(await AppInfo.LatestReleaseAsync(new HttpClient(handler)));
        Assert.Empty(handler.Requests);
        Assert.False(Edition.SelfUpdate);
        Assert.False(Edition.DownloadsFromGitHub);
    }

    [Fact]
    public async Task TheWindowOffersTheSmapiWebsiteInsteadOfDownloadingIt()
    {
        var game = _dir.Sub("game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Stardew Valley.dll"), "x");     // the game is there, SMAPI is not
        var cfg = _dir.Sub("cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "settings.json"), JsonSerializer.Serialize(new { GamePath = game, SyncMode = 0, SyncOnStartup = false, Language = "es" }));
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", cfg);

        var vm = new MainViewModel();
        Assert.True(vm.IsNexusEdition);
        Assert.False(vm.IsNotNexusEdition);
        Assert.EndsWith("Nexus", vm.AppVersionText);
        Assert.Equal("https://www.nexusmods.com/stardewvalley/mods/54058", Edition.NexusPageUrl);
        Assert.True(vm.SmapiBannerVisible);
        Assert.Equal("Abrir la web de SMAPI", vm.SmapiActionText);              // not "Instalar SMAPI"
        Assert.Contains("web oficial", vm.SmapiBannerText);

        await vm.CheckAppUpdateCommand.ExecuteAsync(null);
        Assert.False(vm.NewVersionAvailable);
        Assert.Contains("Nexus", vm.AppUpdateInfo);
        await vm.UpdateAppNowCommand.ExecuteAsync(null);                       // nothing happens, nothing is downloaded
        Assert.False(vm.IsBusy);
    }
}
