using SdvMods.Core.Mods;

namespace SdvMods.Tests;

public class ConfigAndDependantsTests
{
    static InstalledMod Mod(string id, bool enabled = true, string[]? needs = null, string? packFor = null)
    {
        var m = new ModManifest { UniqueID = id, Name = id, Version = "1.0.0" };
        foreach (var n in needs ?? Array.Empty<string>()) m.Dependencies.Add(new ModDependency { UniqueID = n });
        if (packFor is not null) m.ContentPackFor = new ContentPackFor { UniqueID = packFor };
        return new InstalledMod(m, "/mods/" + id, enabled);
    }

    [Fact]
    public void DependantsAreFoundDirectlyAndThroughEachOther()
    {
        var all = new[]
        {
            Mod("Core"), Mod("Lib", needs: new[] { "Core" }), Mod("Pack", packFor: "Lib"),
            Mod("Unrelated"), Mod("OffMod", enabled: false, needs: new[] { "Core" }),
        };
        var deps = ModScanner.FindDependants(all, new[] { "Core" }).Select(m => m.UniqueID).ToArray();
        Assert.Equal(new[] { "Lib", "Pack" }, deps);                       // disabled mods do not count, the target itself is excluded
        Assert.Empty(ModScanner.FindDependants(all, new[] { "Unrelated" }));
        Assert.Empty(ModScanner.FindDependants(all, new[] { "Core", "Lib", "Pack" }));  // everything involved is already in the selection
    }

    [Fact]
    public void OptionalDependenciesDoNotCount()
    {
        var m = new ModManifest { UniqueID = "Soft", Name = "Soft" };
        m.Dependencies.Add(new ModDependency { UniqueID = "Core", IsRequired = false });
        Assert.Empty(ModScanner.FindDependants(new[] { Mod("Core"), new InstalledMod(m, "/m/Soft", true) }, new[] { "Core" }));
    }

    [Fact]
    public void DisabledRequirementsAreFoundTransitively()
    {
        var all = new[] { Mod("App", needs: new[] { "Lib" }), Mod("Lib", enabled: false, needs: new[] { "Core" }), Mod("Core", enabled: false), Mod("Other", enabled: false) };
        Assert.Equal(new[] { "Core", "Lib" }, ModScanner.FindDisabledRequirements(all, new[] { "App" }).Select(m => m.UniqueID).ToArray());
        Assert.Empty(ModScanner.FindDisabledRequirements(all, new[] { "Other" }));
    }

    static string WriteConfig(TempDir t, string json)
    {
        var f = Path.Combine(t.Path, "config.json");
        File.WriteAllText(f, json);
        return f;
    }

    [Fact]
    public void ConfigFormIsBuiltFromTheJson()
    {
        using var t = new TempDir();
        var doc = ModConfigDocument.Load(WriteConfig(t, """
            { "Enabled": true, "Speed": 1.5, "Count": 3, "Name": "Abby", "Keys": ["K", "L"],
              "Advanced": { "Debug": false, "Mode": "fast" }, "Nothing": null }
            """));
        var kinds = doc.Fields.Select(f => (string.Join(".", f.Path), f.Kind)).ToArray();
        Assert.Equal(new[]
        {
            ("Enabled", ConfigKind.Bool), ("Speed", ConfigKind.Number), ("Count", ConfigKind.Number), ("Name", ConfigKind.Text),
            ("Keys", ConfigKind.Json), ("Advanced", ConfigKind.Header), ("Advanced.Debug", ConfigKind.Bool), ("Advanced.Mode", ConfigKind.Text),
            ("Nothing", ConfigKind.Json),
        }, kinds);
    }

    [Fact]
    public void SavingChangesOnlyWhatWasEditedAndKeepsABackup()
    {
        using var t = new TempDir();
        var file = WriteConfig(t, """{ "Enabled": true, "Ratio": 1.0, "Count": 3, "Name": "Abby", "Sub": { "Debug": false } }""");
        var doc = ModConfigDocument.Load(file);
        doc.Fields.Single(f => f.Label == "Enabled").BoolValue = false;
        doc.Fields.Single(f => f.Label == "Count").Text = "7";
        doc.Fields.Single(f => f.Label == "Debug").BoolValue = true;
        Assert.True(doc.TrySave());

        var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.False((bool)saved["Enabled"]!);
        Assert.Equal(7, (int)saved["Count"]!);
        Assert.Equal("Abby", (string)saved["Name"]!);
        Assert.True((bool)saved["Sub"]!["Debug"]!);
        Assert.Contains("\"Ratio\": 1.0", File.ReadAllText(file));          // untouched numbers keep their form
        Assert.True(File.Exists(file + ".bak"));
        Assert.Contains("\"Enabled\": true", File.ReadAllText(file + ".bak"));
    }

    [Fact]
    public void InvalidValuesAreReportedAndNothingIsWritten()
    {
        using var t = new TempDir();
        var file = WriteConfig(t, """{ "Count": 3, "Keys": ["K"] }""");
        var before = File.ReadAllText(file);
        var doc = ModConfigDocument.Load(file);
        doc.Fields.Single(f => f.Label == "Count").Text = "many";
        doc.Fields.Single(f => f.Label == "Keys").Text = "[\"K\",";
        Assert.False(doc.TrySave());
        Assert.All(doc.Fields, f => Assert.NotEqual("", f.Error));
        Assert.Equal(before, File.ReadAllText(file));
        Assert.False(File.Exists(file + ".bak"));
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted_AndBrokenFilesAreRejected()
    {
        using var t = new TempDir();
        var ok = ModConfigDocument.Load(WriteConfig(t, "{ // a comment\n \"A\": 1, }"));
        Assert.Single(ok.Fields);
        Assert.Throws<InvalidDataException>(() => ModConfigDocument.Load(WriteConfig(t, "not json")));
        Assert.Throws<InvalidDataException>(() => ModConfigDocument.Load(WriteConfig(t, "[1,2]")));
    }

    [Fact]
    public void RestoreDefaultsRemovesTheFileButKeepsACopy()
    {
        using var t = new TempDir();
        var file = WriteConfig(t, """{ "A": 1 }""");
        ModConfigDocument.RestoreDefaults(file);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".bak"));
    }
}
