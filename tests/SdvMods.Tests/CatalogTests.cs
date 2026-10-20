using SdvMods.Core.Game;
using SdvMods.Core.Updates;

namespace SdvMods.Tests;

public class CatalogTests
{
    [Fact]
    public void PlainTextStripsHtmlAndBbcode()
    {
        var t = NexusCatalog.ToPlainText("*Hola*<br />\n<br />[b]Negrita[/b] &amp; <a href=\"x\">enlace</a><br /><br /><br /><br />fin");
        Assert.Equal("*Hola*\n\nNegrita & enlace\n\nfin", t);
    }

    [Fact]
    public void SmapiCheckDetectsMissingAndIncomplete()
    {
        using var t = new TempDir();
        Assert.False(SmapiInfo.Check(t.Path, null).Installed);
        File.WriteAllText(Path.Combine(t.Path, "StardewModdingAPI.dll"), "x");
        var st = SmapiInfo.Check(t.Path, new SmapiRelease("4.9.9", "x", "u"));
        Assert.True(st.Installed);
        Assert.NotNull(st.Problem); // no smapi-internal folder
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSearchAndDetail()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        using var http = Http.Create();
        var cat = new NexusCatalog(http);
        var page = await cat.SearchAsync("Content Patcher", CatalogSort.Relevance, count: 5);
        Assert.Contains(page.Mods, m => m.ModId == 1915);
        var many = await cat.GetManyAsync(new[] { 1915, 541 });
        Assert.Equal(2, many.Count);
        var d = await cat.GetDetailAsync(1915);
        Assert.NotNull(d);
        Assert.NotEmpty(d!.Images);
        Assert.NotEmpty(d.Description);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveResolvesMissingNexusMods()
    {
        if (Environment.GetEnvironmentVariable("SDV_LIVE") != "1") return;
        using var http = Http.Create();
        var found = await new UpdateService(http).ResolveMissingAsync(new (string, string, IReadOnlyList<string>)[]
        {
            ("Pathoschild.ContentPatcher", "Content Patcher", new[] { "Nexus:1915" }),
            ("spacechase0.GenericModConfigMenu", "Generic Mod Config Menu", new[] { "Nexus:5098" }),
            ("Unknown.Mod", "Unknown", new[] { "Nexus:999999" }),
        });
        Assert.Equal(3, found.Count);
        Assert.All(found, f => Assert.NotNull(f.NexusModId));
    }
}
