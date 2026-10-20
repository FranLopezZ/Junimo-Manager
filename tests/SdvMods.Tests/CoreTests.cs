using System.IO.Compression;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;
using SdvMods.Core.Sync;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdvtest-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SemVerTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("1.10", "1.9", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("2.0.0", "2.0.0-beta.1", true)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", false)]
    [InlineData("v1.5.1", "1.5.0", true)]
    [InlineData("1.0", "garbage", true)]
    public void IsNewer(string candidate, string installed, bool expected)
        => Assert.Equal(expected, SemVer.IsNewer(candidate, installed));
}

public class ManifestTests
{
    [Fact]
    public void ParsesSmapiStyleJson_WithCommentsBomAndTrailingCommas()
    {
        var json = "﻿{\n // comment\n \"Name\":\"Test\", \"UniqueID\":\"a.b\", \"Version\":\"1.0\",\n \"UpdateKeys\":[\"Nexus:12@main\",], \"Dependencies\":[{\"UniqueID\":\"x.y\",\"IsRequired\":false}],\n}";
        var m = ModManifest.Parse(json);
        Assert.NotNull(m);
        Assert.Equal("a.b", m!.UniqueID);
        Assert.Equal(12, UpdateKey.NexusId(m.UpdateKeys));
        Assert.False(m.Dependencies[0].IsRequired);
    }

    [Fact] public void RejectsManifestWithoutId() => Assert.Null(ModManifest.Parse("{\"Name\":\"x\"}"));
}

public class NxmTests
{
    [Fact]
    public void ParsesLink()
    {
        var l = NxmLink.Parse("nxm://stardewvalley/mods/1915/files/77?key=AbC_d&expires=1700000000&user_id=5");
        Assert.NotNull(l);
        Assert.Equal((1915, 77, "AbC_d", 1700000000L), (l!.ModId, l.FileId, l.Key, l.Expires));
    }
    [Fact] public void RejectsGarbage() => Assert.Null(NxmLink.Parse("nxm://x/collections/abc"));
}

public class InstallerTests
{
    static string MakeZip(TempDir t, string name, params (string path, string content)[] files)
    {
        var zip = t.Sub(name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, c) in files)
        {
            var e = z.CreateEntry(p);
            using var w = new StreamWriter(e.Open());
            w.Write(c);
        }
        return zip;
    }

    static string Manifest(string id, string version, string name = "Mod") =>
        $"{{\"Name\":\"{name}\",\"UniqueID\":\"{id}\",\"Version\":\"{version}\",\"EntryDll\":\"x.dll\"}}";

    [Fact]
    public void InstallsWrappedFlatAndGroupedZips()
    {
        using var t = new TempDir();
        var mods = t.Sub("Mods");
        var inst = new ModInstaller(mods, t.Sub("bk"));

        inst.InstallArchive(MakeZip(t, "a.zip", ("ModA/manifest.json", Manifest("a.a", "1.0")), ("ModA/x.dll", "")));
        inst.InstallArchive(MakeZip(t, "b.zip", ("manifest.json", Manifest("b.b", "1.0", "Flat Mod")), ("x.dll", "")));
        inst.InstallArchive(MakeZip(t, "c.zip",
            ("Pack/C1/manifest.json", Manifest("c.1", "1.0")), ("Pack/C2/manifest.json", Manifest("c.2", "1.0"))));

        var ids = ModScanner.Scan(mods).Select(m => m.UniqueID).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a.a", "b.b", "c.1", "c.2" }, ids);
        Assert.True(Directory.Exists(Path.Combine(mods, "Flat Mod")));   // flat zip gets a folder named after the mod
        Assert.True(Directory.Exists(Path.Combine(mods, "C1")));         // wrapper folder flattened
    }

    [Fact]
    public void UpdateKeepsConfigAndDisabledStateAndBacksUp()
    {
        using var t = new TempDir();
        var mods = t.Sub("Mods");
        var inst = new ModInstaller(mods, t.Sub("bk"));
        inst.InstallArchive(MakeZip(t, "v1.zip", ("M/manifest.json", Manifest("m.m", "1.0")), ("M/x.dll", "old")));
        var installed = ModScanner.Scan(mods).Single();
        File.WriteAllText(Path.Combine(installed.Folder, "config.json"), "{\"mine\":true}");
        ModScanner.SetEnabled(mods, installed, false);

        var res = inst.InstallArchive(MakeZip(t, "v2.zip", ("M-2.0/manifest.json", Manifest("m.m", "2.0")), ("M-2.0/x.dll", "new"), ("M-2.0/config.json", "{}")), "m.m");

        var now = ModScanner.Scan(mods).Single();
        Assert.Equal("2.0", now.Version);
        Assert.False(now.Enabled);                                              // still disabled
        Assert.Equal("{\"mine\":true}", File.ReadAllText(Path.Combine(now.Folder, "config.json")));
        Assert.NotNull(inst.LatestBackup("m.m"));
        Assert.Contains(res.Warnings, w => w.Contains("1.0") && w.Contains("2.0"));
    }

    [Fact]
    public void RefusesWrongArchiveAndPathTraversal()
    {
        using var t = new TempDir();
        var mods = t.Sub("Mods");
        var inst = new ModInstaller(mods, t.Sub("bk"));
        var other = MakeZip(t, "o.zip", ("O/manifest.json", Manifest("other.mod", "1.0")));
        Assert.Throws<InvalidDataException>(() => inst.InstallArchive(other, "wanted.mod"));

        var evil = MakeZip(t, "e.zip", ("../evil.txt", "x"), ("M/manifest.json", Manifest("m.m", "1.0")));
        Assert.Throws<InvalidDataException>(() => inst.InstallArchive(evil));
        Assert.False(File.Exists(t.Sub("evil.txt")));
    }

    [Fact]
    public void ReportsMissingDependencies()
    {
        using var t = new TempDir();
        var inst = new ModInstaller(t.Sub("Mods"), t.Sub("bk"));
        var res = inst.InstallArchive(MakeZip(t, "d.zip", ("D/manifest.json",
            "{\"Name\":\"D\",\"UniqueID\":\"d.d\",\"Version\":\"1.0\",\"Dependencies\":[{\"UniqueID\":\"need.me\"}]}")));
        Assert.Contains(res.Warnings, w => w.Contains("need.me"));
    }
}

public class ProfileTests
{
    static InstalledMod Mod(string id, string v, bool enabled) =>
        new(new ModManifest { UniqueID = id, Name = id, Version = v }, "/x/" + id, enabled);

    [Fact]
    public void PlanFindsMissingToggleAndExtras()
    {
        var p = new Profile { Mods = { new() { UniqueID = "a", Version = "2.0", Enabled = true }, new() { UniqueID = "b", Version = "1.0", Enabled = false }, new() { UniqueID = "c", Version = "1.0", Enabled = true } } };
        var plan = ProfileService.Plan(p, new[] { Mod("a", "1.0", true), Mod("b", "1.0", true), Mod("z", "1.0", true), Mod("SMAPI.ConsoleCommands", "4", true) });

        Assert.Equal(new[] { "c" }, plan.ToInstall.Select(m => m.UniqueID));
        Assert.Contains(plan.ToToggle, x => x.Mod.UniqueID == "b" && !x.Enable);
        Assert.Contains(plan.ToToggle, x => x.Mod.UniqueID == "z" && !x.Enable);   // not in profile -> disabled
        Assert.DoesNotContain(plan.ToToggle, x => x.Mod.UniqueID.StartsWith("SMAPI"));
        Assert.Single(plan.Outdated);
    }
}
