using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;

namespace SdvMods.Tests;

public class ProfileMembershipTests
{
    static InstalledMod Mod(string id, string version = "1.0.0", bool enabled = true, string? name = null)
        => new(new ModManifest { UniqueID = id, Name = name ?? id, Version = version, UpdateKeys = { "Nexus:1" } }, "/mods/" + id, enabled);

    [Fact]
    public void ProfilesAreASelectionOfTheModsOnDisk()
    {
        using var t = new TempDir();
        var svc = new ProfileService(Path.Combine(t.Path, "profiles.json"));
        var farm = svc.CreateEmpty("Granja");
        var onDisk = new[] { Mod("A.One"), Mod("B.Two"), Mod("C.Three") };

        svc.UpsertMod(farm, onDisk[0], true);
        svc.UpsertMod(farm, onDisk[1], false);
        Assert.True(ProfileService.IsMember(farm, "A.One"));
        Assert.False(ProfileService.IsMember(farm, "C.Three"));   // on disk, but not part of this profile

        // The plan for activating it: keep C off, B off, A on; nothing to install.
        var plan = ProfileService.Plan(farm, onDisk);
        Assert.Empty(plan.ToInstall);
        Assert.Contains(plan.ToToggle, x => x.Mod.UniqueID == "B.Two" && !x.Enable);
        Assert.Contains(plan.ToToggle, x => x.Mod.UniqueID == "C.Three" && !x.Enable);

        // Removing from the profile leaves the file alone for other profiles.
        Assert.True(svc.RemoveMod(farm, "A.One"));
        Assert.False(svc.RemoveMod(farm, "A.One"));
        Assert.False(ProfileService.IsMember(farm, "A.One"));
    }

    [Fact]
    public void RefreshMembersFollowsTheDiskButNeverAddsOrDropsMods()
    {
        using var t = new TempDir();
        var svc = new ProfileService(Path.Combine(t.Path, "profiles.json"));
        var p = svc.CreateEmpty("P");
        svc.UpsertMod(p, Mod("A.One", "1.0.0"), true);
        svc.UpsertMod(p, Mod("Missing.Mod"), true);              // waiting to be installed on this device

        var changed = svc.RefreshMembers(p, new[] { Mod("A.One", "1.1.0", enabled: false), Mod("Extra.Mod") });
        Assert.True(changed);
        Assert.Equal(2, p.Mods.Count);                           // Extra.Mod was not added, Missing.Mod was not dropped
        var one = p.Mods.Single(m => m.UniqueID == "A.One");
        Assert.Equal(("1.1.0", false), (one.Version, one.Enabled));
        Assert.False(svc.RefreshMembers(p, new[] { Mod("A.One", "1.1.0", enabled: false) })); // nothing left to change
    }

    [Fact]
    public void EmptyProfileSurvivesSaveAndReload()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "profiles.json");
        new ProfileService(file).CreateEmpty("Vanilla");
        var again = new ProfileService(file);
        Assert.Equal("Vanilla", Assert.Single(again.Active).Name);
    }
}
