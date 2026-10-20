using System.Text.Json;
using SdvMods.App.ViewModels;
using SdvMods.Core.Profiles;

namespace SdvMods.Tests;

/// <summary>The dependants / requirements dialog and the config.json editor, driven through the real MainViewModel.</summary>
[Collection("ViewModel")]
public sealed class DependantsAndConfigViewModelTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly string? _oldConfig = Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR");
    readonly string _game;

    public DependantsAndConfigViewModelTests()
    {
        _game = _dir.Sub("game");
        Directory.CreateDirectory(_game);
        File.WriteAllText(Path.Combine(_game, "Stardew Valley.dll"), "x");
        File.WriteAllText(Path.Combine(_game, "StardewModdingAPI.dll"), "x");
        Directory.CreateDirectory(Path.Combine(_game, "smapi-internal"));

        void Mod(string folder, string id, string? needs = null, string? packFor = null)
        {
            var d = Path.Combine(_game, "Mods", folder);
            Directory.CreateDirectory(d);
            var manifest = new Dictionary<string, object?> { ["Name"] = "Mod " + id, ["Author"] = "me", ["Version"] = "1.0.0", ["UniqueID"] = id };
            if (needs is not null) manifest["Dependencies"] = new[] { new { UniqueID = needs } };
            if (packFor is not null) manifest["ContentPackFor"] = new { UniqueID = packFor };
            File.WriteAllText(Path.Combine(d, "manifest.json"), JsonSerializer.Serialize(manifest));
        }
        Mod("Lib", "T.Lib");
        Mod("App", "T.App", needs: "T.Lib");
        Mod("Pack", "T.Pack", packFor: "T.App");
        Mod("Solo", "T.Solo");
        File.WriteAllText(Path.Combine(_game, "Mods", "App", "config.json"), """{ "Enabled": true, "Speed": 2, "Sub": { "Name": "abby" } }""");

        var cfg = _dir.Sub("cfg");
        Directory.CreateDirectory(cfg);
        ProfileMod M(string id) => new() { UniqueID = id, Name = "Mod " + id, Version = "1.0.0", Enabled = true };
        var doc = new ProfilesDocument { Profiles = { new Profile { Id = "p", Name = "P", Mods = { M("T.Lib"), M("T.App"), M("T.Pack"), M("T.Solo") } } } };
        File.WriteAllText(Path.Combine(cfg, "profiles.json"), doc.ToJson());
        File.WriteAllText(Path.Combine(cfg, "settings.json"),
            JsonSerializer.Serialize(new { GamePath = _game, SyncMode = 0, SyncOnStartup = false, ActiveProfileId = "p", Language = "es" }));
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

    static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out waiting for the UI state");
    }

    /// <summary>Runs an action that is expected to open the dialog, answers it, and waits for the action to finish.</summary>
    static async Task<string[]> AnswerAsync(MainViewModel vm, Task action, Action answer)
    {
        await WaitForAsync(() => vm.DialogVisible);
        Assert.Equal("DialogOverlay", vm.ModalOverlayName);
        var shown = vm.DialogItems.ToArray();
        answer();
        await action;
        Assert.False(vm.DialogVisible);
        return shown;
    }

    [Fact]
    public async Task DisablingAModListsEveryModThatNeedsItAndCanIncludeThem()
    {
        var vm = await StartAsync();
        var task = vm.ToggleTargetAsync("Mod T.Lib");
        var shown = await AnswerAsync(vm, task, () => vm.DialogPrimaryCommand.Execute(null));
        Assert.Equal(new[] { "Mod T.App  1.0.0", "Mod T.Pack  1.0.0" }, shown);       // the content pack needs App, which needs Lib
        Assert.True(new[] { ".App", ".Lib", ".Pack", "Solo" }.SequenceEqual(OnDisk()), $"disk: {string.Join(",", OnDisk())} | status: {vm.Status}");
    }

    [Fact]
    public async Task ChoosingOnlyTheSelectedModLeavesTheOthersAlone()
    {
        var vm = await StartAsync();
        var task = vm.ToggleTargetAsync("Mod T.Lib");
        await AnswerAsync(vm, task, () => vm.DialogSecondaryCommand.Execute(null));
        Assert.Equal(new[] { ".Lib", "App", "Pack", "Solo" }, OnDisk());
    }

    [Fact]
    public async Task CancellingChangesNothing()
    {
        var vm = await StartAsync();
        var task = vm.ToggleTargetAsync("Mod T.Lib");
        await AnswerAsync(vm, task, () => vm.DialogCancelCommand.Execute(null));
        Assert.Equal(new[] { "App", "Lib", "Pack", "Solo" }, OnDisk());
    }

    [Fact]
    public async Task ModsWithoutDependantsAreToggledWithoutAsking()
    {
        var vm = await StartAsync();
        await vm.ToggleTargetAsync("Mod T.Solo");
        Assert.False(vm.DialogVisible);
        Assert.Contains(".Solo", OnDisk());
    }

    [Fact]
    public async Task EnablingAModOffersToEnableWhatItNeeds()
    {
        var vm = await StartAsync();
        await vm.DisableAllCommand.ExecuteAsync(null);
        var disableAll = Task.CompletedTask;
        Assert.Equal(new[] { ".App", ".Lib", ".Pack", ".Solo" }, OnDisk());
        _ = disableAll;

        var task = vm.ToggleTargetAsync("Mod T.Pack");                                 // needs App, which needs Lib
        var shown = await AnswerAsync(vm, task, () => vm.DialogPrimaryCommand.Execute(null));
        Assert.Equal(new[] { "Mod T.App  1.0.0", "Mod T.Lib  1.0.0" }, shown);
        Assert.Equal(new[] { "App", "Lib", "Pack", ".Solo" }.OrderBy(x => x).ToArray(), OnDisk());
    }

    [Fact]
    public async Task ConfigEditorShowsFieldsSavesThemAndKeepsABackup()
    {
        var vm = await StartAsync();
        var app = vm.Mods.Single(m => m.Name == "Mod T.App");
        Assert.True(app.HasConfig);
        Assert.False(vm.Mods.Single(m => m.Name == "Mod T.Solo").HasConfig);

        app.ConfigCommand.Execute(null);
        Assert.True(vm.ConfigVisible);
        Assert.Equal("ConfigOverlay", vm.ModalOverlayName);
        Assert.Equal(new[] { "Enabled", "Speed", "Sub", "Name" }, vm.ConfigFields.Select(f => f.Label).ToArray());

        vm.ConfigFields.Single(f => f.Label == "Enabled").BoolValue = false;
        vm.ConfigFields.Single(f => f.Label == "Speed").Text = "fast";               // not a number: nothing is written
        vm.SaveConfigCommand.Execute(null);
        Assert.True(vm.ConfigVisible);
        Assert.True(vm.ConfigFields.Single(f => f.Label == "Speed").HasError);

        vm.ConfigFields.Single(f => f.Label == "Speed").Text = "5";
        vm.SaveConfigCommand.Execute(null);
        Assert.False(vm.ConfigVisible);
        var file = Path.Combine(_game, "Mods", "App", "config.json");
        var saved = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Assert.False(saved.GetProperty("Enabled").GetBoolean());
        Assert.Equal(5, saved.GetProperty("Speed").GetInt32());
        Assert.Equal("abby", saved.GetProperty("Sub").GetProperty("Name").GetString());
        Assert.True(File.Exists(file + ".bak"));
    }

    [Fact]
    public async Task RestoringDefaultsNeedsConfirmationAndDeletesTheFile()
    {
        var vm = await StartAsync();
        vm.Mods.Single(m => m.Name == "Mod T.App").ConfigCommand.Execute(null);
        var file = Path.Combine(_game, "Mods", "App", "config.json");
        await vm.RestoreConfigDefaultsCommand.ExecuteAsync(null);                     // first press only asks
        Assert.True(File.Exists(file));
        await vm.RestoreConfigDefaultsCommand.ExecuteAsync(null);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".bak"));
        Assert.False(vm.ConfigVisible);
        Assert.False(vm.Mods.Single(m => m.Name == "Mod T.App").HasConfig);
    }
}
