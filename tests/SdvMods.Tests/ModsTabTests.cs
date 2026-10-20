using System.Text.Json;
using SdvMods.App.ViewModels;
using SdvMods.Core.Profiles;

namespace SdvMods.Tests;

/// <summary>Drives the real MainViewModel against a fake game folder: profile switching, selection and batch actions.</summary>
[Collection("ViewModel")]
public sealed class ModsTabTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR");
    readonly string _game;

    public ModsTabTests()
    {
        _game = _dir.Sub("game");
        Directory.CreateDirectory(_game);
        File.WriteAllText(Path.Combine(_game, "Stardew Valley.dll"), "x");
        File.WriteAllText(Path.Combine(_game, "StardewModdingAPI.dll"), "x");
        Directory.CreateDirectory(Path.Combine(_game, "smapi-internal"));
        foreach (var (folder, id) in new[] { ("A", "T.A"), (".B", "T.B"), ("C", "T.C"), ("D", "T.D") })
        {
            var d = Path.Combine(_game, "Mods", folder);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "manifest.json"),
                JsonSerializer.Serialize(new { Name = "Mod " + id, Author = "me", Version = "1.0.0", UniqueID = id, UpdateKeys = new[] { "Nexus:1" } }));
        }

        var cfg = _dir.Sub("cfg");
        Directory.CreateDirectory(cfg);
        ProfileMod M(string id, bool on) => new() { UniqueID = id, Name = "Mod " + id, Version = "1.0.0", Enabled = on, UpdateKeys = { "Nexus:1" } };
        var doc = new ProfilesDocument
        {
            Profiles =
            {
                new Profile { Id = "p1", Name = "Farm", Mods = { M("T.A", true), M("T.B", false), M("T.C", true) } },
                new Profile { Id = "p2", Name = "Vanilla" },
            },
        };
        File.WriteAllText(Path.Combine(cfg, "profiles.json"), doc.ToJson());
        File.WriteAllText(Path.Combine(cfg, "settings.json"),
            JsonSerializer.Serialize(new { GamePath = _game, SyncMode = 0, SyncOnStartup = false, ActiveProfileId = "p1", Language = "es" }));
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", cfg);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", _oldConfig);
        _dir.Dispose();
    }

    string[] OnDisk() => Directory.GetDirectories(Path.Combine(_game, "Mods")).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;

    static async Task<MainViewModel> StartAsync()
    {
        var vm = new MainViewModel();
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task MainTabShowsOnlyTheActiveProfileUntilYouAskForAll()
    {
        var vm = await StartAsync();
        Assert.Equal(new[] { "Mod T.A", "Mod T.B", "Mod T.C" }, vm.Mods.Select(m => m.Name).ToArray());   // D is installed but not in "Farm"
        vm.ModScopeIndex = 1;
        Assert.Equal(4, vm.Mods.Count);
        Assert.False(vm.Mods.Single(m => m.Name == "Mod T.D").InProfile);
        vm.ModScopeIndex = 0;
        vm.ModFilterText = "T.B";
        Assert.Equal("Mod T.B", Assert.Single(vm.Mods).Name);
    }

    [Fact]
    public async Task DisableAllThenEnableAllUpdatesDiskAndProfile()
    {
        var vm = await StartAsync();
        await vm.DisableAllCommand.ExecuteAsync(null);
        Assert.Equal(new[] { ".A", ".B", ".C", "D" }, OnDisk());   // D is not in the profile, so "all" never touched it
        Assert.All(new ProfileService(Path.Combine(_dir.Sub("cfg"), "profiles.json")).Find("p1")!.Mods, m => Assert.False(m.Enabled));

        await vm.EnableAllCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "A", "B", "C", "D" }, OnDisk());
    }

    [Fact]
    public async Task SelectionActionsAddRemoveAndUninstall()
    {
        var vm = await StartAsync();
        vm.ModScopeIndex = 1;
        vm.Mods.Single(m => m.Name == "Mod T.D").IsSelected = true;
        Assert.Equal(1, vm.SelectedCount);

        await vm.AddSelectedToProfileCommand.ExecuteAsync(null);                     // D joins the profile
        var profile = () => new ProfileService(Path.Combine(_dir.Sub("cfg"), "profiles.json")).Find("p1")!;
        Assert.Contains(profile().Mods, m => m.UniqueID == "T.D");
        Assert.Equal(0, vm.SelectedCount);                                           // selection is cleared afterwards

        vm.Mods.Single(m => m.Name == "Mod T.A").IsSelected = true;
        await vm.RemoveSelectedFromProfileCommand.ExecuteAsync(null);                // A leaves the profile and stops loading, but stays on disk
        Assert.DoesNotContain(profile().Mods, m => m.UniqueID == "T.A");
        Assert.Contains(".A", OnDisk());

        vm.Mods.Single(m => m.Name == "Mod T.A").IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);                        // first press only asks for confirmation
        Assert.Contains(".A", OnDisk());
        Assert.True(vm.ConfirmBatchUninstall);
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.DoesNotContain(".A", OnDisk());
    }

    [Fact]
    public async Task SwitchingToAnEmptyProfileTurnsEverythingOff()
    {
        var vm = await StartAsync();
        vm.ActiveProfileChoice = vm.Profiles.Single(p => p.Name == "Vanilla");       // applied after a short pause
        for (var i = 0; i < 40 && vm.ActiveProfileText != "Perfil: Vanilla"; i++) await Task.Delay(100);
        for (var i = 0; i < 40 && vm.IsBusy; i++) await Task.Delay(100);
        Assert.Equal("Perfil: Vanilla", vm.ActiveProfileText);
        Assert.Equal(new[] { ".A", ".B", ".C", ".D" }, OnDisk());
        Assert.Empty(vm.Mods);                                                       // the list shows this profile's (no) mods
    }
}

[CollectionDefinition("ViewModel", DisableParallelization = true)]
public sealed class ViewModelCollection { }
