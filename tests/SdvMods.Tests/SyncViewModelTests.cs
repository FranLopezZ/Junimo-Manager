using System.Text.Json;
using SdvMods.App.ViewModels;
using SdvMods.Core.Profiles;

namespace SdvMods.Tests;

/// <summary>Two "devices" (two settings folders and two game folders) sharing a folder, driven through the real MainViewModel.</summary>
[Collection("ViewModel")]
public sealed class SyncViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", _oldConfig);
        _dir.Dispose();
    }

    string CfgOf(string device) => _dir.Sub(device + "/cfg");
    string ModsOf(string device) => Path.Combine(_dir.Sub(device + "/game"), "Mods");
    string[] OnDisk(string device) => Directory.GetDirectories(ModsOf(device)).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;

    void SetUpDevice(string device)
    {
        var game = _dir.Sub(device + "/game");
        Directory.CreateDirectory(Path.Combine(game, "smapi-internal"));
        File.WriteAllText(Path.Combine(game, "Stardew Valley.dll"), "x");
        File.WriteAllText(Path.Combine(game, "StardewModdingAPI.dll"), "x");
        foreach (var id in new[] { "M1", "M2" })
        {
            var d = Path.Combine(game, "Mods", id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "manifest.json"), JsonSerializer.Serialize(new { Name = "Mod " + id, Author = "x", Version = "1.0.0", UniqueID = "T." + id }));
        }
        Directory.CreateDirectory(CfgOf(device));
        ProfileMod M(string id) => new() { UniqueID = "T." + id, Name = "Mod " + id, Version = "1.0.0", Enabled = true };
        File.WriteAllText(Path.Combine(CfgOf(device), "profiles.json"),
            new ProfilesDocument { Profiles = { new Profile { Id = "farm", Name = "Farm", Mods = { M("M1"), M("M2") } } } }.ToJson());
        File.WriteAllText(Path.Combine(CfgOf(device), "settings.json"), JsonSerializer.Serialize(new
        {
            GamePath = game, SyncMode = 2, SyncFolder = _dir.Sub("shared"), SyncOnStartup = true, SyncAuto = false, ActiveProfileId = "farm", Language = "es",
        }));
    }

    static async Task<MainViewModel> StartAsync(string cfg)
    {
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", cfg);
        var vm = new MainViewModel();
        await vm.InitializeAsync();
        return vm;
    }

    static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out waiting for the UI state");
    }

    [Fact]
    public async Task AChangeOnOneDeviceArrivesOnTheOtherAndConflictsAskBeforeOverwriting()
    {
        SetUpDevice("a"); SetUpDevice("b");

        // A starts: its profile goes up. B starts with identical content: nothing to decide.
        var a = await StartAsync(CfgOf("a"));
        Assert.True(File.Exists(Path.Combine(_dir.Sub("shared"), "junimo-profile-farm.json")));
        var b = await StartAsync(CfgOf("b"));
        Assert.False(b.SyncHasConflicts);
        Assert.Contains("al día", b.SyncStatusText);

        // A turns Mod M1 off and uploads it.
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("a"));
        await a.ToggleTargetAsync("Mod M1");
        await a.PushSyncCommand.ExecuteAsync(null);
        Assert.Contains(".M1", OnDisk("a"));

        // B (nothing changed there) downloads it, and because "Farm" is its active profile the change is applied to its game folder.
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("b"));
        await b.PullSyncCommand.ExecuteAsync(null);
        Assert.Contains(".M1", OnDisk("b"));
        Assert.DoesNotContain(".M2", OnDisk("b"));

        // Now both change the same profile: A turns M2 off, B turns M2 off AND M1 back on → conflict.
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("a"));
        await a.ToggleTargetAsync("Mod M2");
        await a.PushSyncCommand.ExecuteAsync(null);
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("b"));
        await b.ToggleTargetAsync("Mod M1");

        var pull = b.PullSyncCommand.ExecuteAsync(null);                       // asks about the conflict instead of overwriting
        await WaitForAsync(() => b.DialogVisible);
        Assert.Contains("Farm", b.DialogTitle);
        Assert.NotEmpty(b.DialogItems);
        Assert.Contains("M1", OnDisk("b"));                                    // still B's version
        b.DialogSecondaryCommand.Execute(null);                                // "keep mine"
        await pull;
        Assert.False(b.SyncHasConflicts);

        // A receives B's version.
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("a"));
        await a.PullSyncCommand.ExecuteAsync(null);
        Assert.Contains("M1", OnDisk("a"));                                    // B's version won: M1 is on again
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task ARejectedGitHubTokenShowsAClearMessageAndOffersANewSignIn()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        SetUpDevice("a");
        // Gist mode with a token GitHub will refuse (like an expired one with no refresh token): 401 from the real API.
        var cfg = Path.Combine(CfgOf("a"), "settings.json");
        var settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(cfg))!;
        settings["SyncMode"] = 1;
        File.WriteAllText(cfg, JsonSerializer.Serialize(settings));
        // The token is stored protected, so let the app save it the normal way.
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", CfgOf("a"));
        var loaded = SdvMods.Core.Settings.AppSettings.Load();
        loaded.GitHubToken = "gho_thisTokenIsNotValid0000000000000000";
        loaded.Save();

        var vm = new MainViewModel();
        Assert.True(vm.GitHubLoggedIn);
        await vm.CheckSyncCommand.ExecuteAsync(null);
        Assert.Contains("GitHub", vm.Status);
        Assert.False(vm.GitHubLoggedIn);                   // the dead login was forgotten: the sign-in buttons are available again
        Assert.Equal("", vm.GitHubToken);
    }
}
