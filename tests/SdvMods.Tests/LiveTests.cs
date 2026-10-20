using SdvMods.Core.Mods;
using SdvMods.Core.Updates;
using Xunit.Abstractions;

namespace SdvMods.Tests;

/// <summary>Hits the real SMAPI/GitHub services. Run with: SDV_LIVE=1 dotnet test --filter Live</summary>
public class LiveTests(ITestOutputHelper output)
{
    static InstalledMod Fake(string id, string name, params string[] keys) =>
        new(new ModManifest { UniqueID = id, Name = name, Version = "0.0.1", UpdateKeys = keys.ToList() }, "/x/" + id, true);

    [Fact]
    [Trait("Category", "Live")]
    public async Task DownloadsAndInstallsRealMod()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        using var t = new TempDir();
        using var http = Http.Create();
        var ups = await new UpdateService(http).CheckAsync(new[] { Fake("Annosz.UiInfoSuite2", "UI Info Suite 2", "GitHub:Annosz/UiInfoSuite2") });
        var u = Assert.Single(ups);
        var file = Path.Combine(t.Path, u.FileName!);
        await Http.DownloadAsync(http, u.DownloadUrl!, file);
        var inst = new ModInstaller(t.Sub("Mods"), t.Sub("bk"));
        var res = inst.InstallArchive(file, u.UniqueID);
        var mods = ModScanner.Scan(t.Sub("Mods"));
        output.WriteLine(string.Join(", ", mods.Select(m => $"{m.Name} {m.Version} @ {Path.GetFileName(m.Folder)}")) + " | " + string.Join("; ", res.Warnings));
        Assert.Contains(mods, m => m.UniqueID == "Annosz.UiInfoSuite2");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task ResolvesRealMods()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        using var http = Http.Create();
        var svc = new UpdateService(http);
        var ups = await svc.CheckAsync(new[]
        {
            Fake("Pathoschild.ContentPatcher", "Content Patcher", "Nexus:1915", "GitHub:Pathoschild/StardewMods"),
            Fake("spacechase0.GenericModConfigMenu", "GMCM", "Nexus:5098"),
            Fake("Annosz.UiInfoSuite2", "UI Info Suite 2", "Nexus:25239", "GitHub:Annosz/UiInfoSuite2"),
            Fake("focustense.StardewUI", "StardewUI", "GitHub:focustense/StardewUI"),
            Fake("leclair.bettergamemenu", "Better Game Menu", "Nexus:918", "GitHub:KhloeLeclair/StardewMods"),
            Fake("Shockah.ProjectFluent", "Project Fluent", "GitHub:Shockah/Stardew-Valley-Mods"),
        });
        foreach (var u in ups) output.WriteLine($"{u.Name}: {u.CurrentVersion} -> {u.NewVersion} [{u.Method}] {u.DownloadUrl ?? u.PageUrl}");
        Assert.NotEmpty(ups);
    }
}
