using System.Text.Json;
using SdvMods.Core.Profiles;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

public class ShareTests
{
    static Profile Sample() => new()
    {
        Name = "Granja con amigos",
        Mods = { new ProfileMod { UniqueID = "A.B", Name = "Mod B", Version = "1.2.3", Enabled = false, UpdateKeys = { "Nexus:123" } } },
    };

    [Fact]
    public void CodeRoundTripKeepsModsAndGivesNewId()
    {
        var src = Sample();
        var code = ProfileShare.ToCode(src);
        Assert.StartsWith(ProfileShare.Prefix, code);
        var back = ProfileShare.Import("  " + code + "\n");
        Assert.Equal(src.Name, back.Name);
        Assert.NotEqual(src.Id, back.Id);
        var m = Assert.Single(back.Mods);
        Assert.Equal(("A.B", "1.2.3", false, "Nexus:123"), (m.UniqueID, m.Version, m.Enabled, m.UpdateKeys[0]));
    }

    [Fact]
    public void JsonFileRoundTrips()
    {
        var back = ProfileShare.Import(ProfileShare.ToJson(Sample()));
        Assert.Single(back.Mods);
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("JUNIMO1:%%%")]
    [InlineData("{\"foo\":1}")]
    public void GarbageIsRejected(string text) => Assert.Throws<InvalidDataException>(() => ProfileShare.Import(text));

    [Fact]
    public void ParsesReleaseAndComparesVersions()
    {
        using var doc = JsonDocument.Parse("""
            {"tag_name":"v99.0.0","html_url":"https://x/r","body":"notas","assets":[
              {"name":"JunimoManager-99.0.0-win-x64.zip","browser_download_url":"https://x/win.zip"},
              {"name":"JunimoManager-99.0.0-linux-x64.zip","browser_download_url":"https://x/linux.zip"}]}
            """);
        var r = AppInfo.ParseRelease(doc.RootElement)!;
        Assert.Equal("99.0.0", r.Version);
        Assert.Equal(OperatingSystem.IsWindows() ? "https://x/win.zip" : "https://x/linux.zip", r.AssetUrl);
        Assert.Equal("https://x/win.zip", AppInfo.ParseRelease(doc.RootElement, windows: true)!.AssetUrl);
        Assert.Equal("https://x/linux.zip", AppInfo.ParseRelease(doc.RootElement, windows: false)!.AssetUrl);
        Assert.True(AppInfo.IsNewer(r));
        Assert.False(AppInfo.IsNewer(r with { Version = "0.0.1" }));
    }

    [Fact]
    public void VersionComesFromFileNameWhenTagIsNotAVersion()
    {
        using var doc = JsonDocument.Parse("""
            {"tag_name":"latest","html_url":"https://x/r","assets":[
              {"name":"JunimoManager-9.1.0-win-x64.zip","browser_download_url":"https://x/win.zip"},
              {"name":"JunimoManager-9.1.0-linux-x64.zip","browser_download_url":"https://x/linux.zip"}]}
            """);
        Assert.Equal("9.1.0", AppInfo.ParseRelease(doc.RootElement)!.Version);
    }

    [Fact]
    public void FileSwapReplacesAFileThatIsInUse()
    {
        using var t = new TempDir();
        var dst = Path.Combine(t.Path, "app.exe");
        var src = Path.Combine(t.Path, "new.exe");
        File.WriteAllText(dst, "old");
        File.WriteAllText(src, "new");
        using (File.Open(dst, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) // like a running exe
            FileSwap.Replace(src, dst, isExe: true);
        Assert.Equal("new", File.ReadAllText(dst));
    }
}
