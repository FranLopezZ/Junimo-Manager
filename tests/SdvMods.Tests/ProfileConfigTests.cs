using System.Text.Json;
using SdvMods.App.ViewModels;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;

namespace SdvMods.Tests;

public class ProfileConfigStoreTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Dir = new();
        public ProfileConfigStore Store;
        public Profile Profile = new() { Id = "p1", Name = "P", Mods = { new ProfileMod { UniqueID = "T.A" }, new ProfileMod { UniqueID = "T.B" } } };
        public List<InstalledMod> Installed = new();

        public Fixture()
        {
            Store = new ProfileConfigStore(Dir.Sub("store"));
            foreach (var id in new[] { "T.A", "T.B", "T.Other" })
            {
                var folder = Dir.Sub("Mods/" + id);
                Directory.CreateDirectory(folder);
                Installed.Add(new InstalledMod(new ModManifest { UniqueID = id, Name = id }, folder, true));
            }
        }

        public string Path(string id) => System.IO.Path.Combine(Dir.Sub("Mods/" + id), "config.json");
        public void Write(string id, string text) => File.WriteAllText(Path(id), text);
        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void CaptureStoresOnlyTheProfilesModsAndMirrorsTheDisk()
    {
        using var f = new Fixture();
        f.Write("T.A", "{\"x\":1}");
        f.Write("T.Other", "{\"x\":2}");                              // not part of the profile
        Assert.Equal(1, f.Store.Capture(f.Profile, f.Installed));
        Assert.True(f.Store.Has("p1", "T.A"));
        Assert.False(f.Store.Has("p1", "T.Other"));
        Assert.Equal(0, f.Store.Capture(f.Profile, f.Installed));    // nothing changed

        File.Delete(f.Path("T.A"));                                    // "restore defaults" on the mod
        Assert.Equal(1, f.Store.Capture(f.Profile, f.Installed));
        Assert.False(f.Store.Has("p1", "T.A"));
    }

    [Fact]
    public void ApplyRestoresStoredConfigsAndAdoptsTheRest()
    {
        using var f = new Fixture();
        f.Write("T.A", "{\"level\":1}");
        f.Store.Capture(f.Profile, f.Installed);                       // profile remembers level 1
        f.Write("T.A", "{\"level\":9}");                               // another profile / the game changed it
        f.Write("T.B", "{\"b\":true}");                                // nothing stored for B yet

        Assert.Equal(1, f.Store.Apply(f.Profile, f.Installed));
        Assert.Equal("{\"level\":1}", File.ReadAllText(f.Path("T.A")));
        Assert.Equal("{\"b\":true}", File.ReadAllText(f.Path("T.B"))); // untouched…
        Assert.True(f.Store.Has("p1", "T.B"));                         // …and now adopted by the profile
    }

    [Fact]
    public void DuplicateAndDeleteWorkOnTheWholeStore()
    {
        using var f = new Fixture();
        f.Write("T.A", "{\"a\":1}");
        f.Store.Capture(f.Profile, f.Installed);
        f.Store.Copy("p1", "p2");
        Assert.True(f.Store.Has("p2", "T.A"));
        f.Store.Delete("p1");
        Assert.False(f.Store.Has("p1", "T.A"));
        Assert.True(f.Store.Has("p2", "T.A"));
    }

    [Fact]
    public void ExportedConfigsTravelInTheFileButNotInTheCode()
    {
        using var f = new Fixture();
        f.Write("T.A", "{\"a\":1}");
        f.Store.Capture(f.Profile, f.Installed);
        var configs = f.Store.ReadAll(f.Profile);

        var (back, got) = ProfileShare.ImportWithConfigs(ProfileShare.ToJson(f.Profile, configs));
        Assert.Equal("{\"a\":1}", got["T.A"]);
        Assert.Equal(2, back.Mods.Count);

        var (_, fromCode) = ProfileShare.ImportWithConfigs(ProfileShare.ToCode(f.Profile));
        Assert.Empty(fromCode);
        Assert.Empty(ProfileShare.ImportWithConfigs(ProfileShare.ToJson(f.Profile)).Configs);   // older files / no configs
    }
}

[Collection("ViewModel")]
public sealed class ProfileConfigViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR");
    readonly string _modConfig;

    public ProfileConfigViewModelTests()
    {
        var game = _dir.Sub("game");
        Directory.CreateDirectory(Path.Combine(game, "smapi-internal"));
        File.WriteAllText(Path.Combine(game, "Stardew Valley.dll"), "x");
        File.WriteAllText(Path.Combine(game, "StardewModdingAPI.dll"), "x");
        var mod = Path.Combine(game, "Mods", "A");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "manifest.json"), JsonSerializer.Serialize(new { Name = "A", Author = "x", Version = "1.0.0", UniqueID = "T.A" }));
        _modConfig = Path.Combine(mod, "config.json");
        File.WriteAllText(_modConfig, "{\"Level\":1}");

        var cfg = _dir.Sub("cfg");
        Directory.CreateDirectory(cfg);
        ProfileMod M() => new() { UniqueID = "T.A", Name = "A", Version = "1.0.0", Enabled = true };
        var doc = new ProfilesDocument { Profiles = { new Profile { Id = "farm", Name = "Farm", Mods = { M() } }, new Profile { Id = "alt", Name = "Alt", Mods = { M() } } } };
        File.WriteAllText(Path.Combine(cfg, "profiles.json"), doc.ToJson());
        File.WriteAllText(Path.Combine(cfg, "settings.json"),
            JsonSerializer.Serialize(new { GamePath = game, SyncMode = 0, SyncOnStartup = false, ActiveProfileId = "farm", Language = "es" }));
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", cfg);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JUNIMO_CONFIG_DIR", _oldConfig);
        _dir.Dispose();
    }

    static async Task SwitchToAsync(MainViewModel vm, string name)
    {
        vm.ActiveProfileChoice = vm.Profiles.Single(p => p.Name == name);
        for (var i = 0; i < 60 && vm.ActiveProfileText != "Perfil: " + name; i++) await Task.Delay(100);
        for (var i = 0; i < 60 && vm.IsBusy; i++) await Task.Delay(100);
        Assert.Equal("Perfil: " + name, vm.ActiveProfileText);
    }

    [Fact]
    public async Task EachProfileKeepsItsOwnModSettings()
    {
        var vm = new MainViewModel();
        await vm.InitializeAsync();
        await Task.Delay(300);                                            // the startup capture runs in the background

        File.WriteAllText(_modConfig, "{\"Level\":5}");                   // the game changed it while "Farm" was active
        await SwitchToAsync(vm, "Alt");                                   // Farm remembers 5; Alt adopts what is there
        Assert.Equal("{\"Level\":5}", File.ReadAllText(_modConfig));

        File.WriteAllText(_modConfig, "{\"Level\":9}");                   // settings made while "Alt" is active
        await SwitchToAsync(vm, "Farm");
        Assert.Equal("{\"Level\":5}", File.ReadAllText(_modConfig));      // Farm got its own back

        await SwitchToAsync(vm, "Alt");
        Assert.Equal("{\"Level\":9}", File.ReadAllText(_modConfig));
    }
}
