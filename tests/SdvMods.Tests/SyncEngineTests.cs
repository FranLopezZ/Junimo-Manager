using System.Text.Json;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;
using SdvMods.Core.Sync;

namespace SdvMods.Tests;

public sealed class SyncEngineTests : IDisposable
{
    readonly TempDir _dir = new();
    readonly FolderProfileStore _shared;

    sealed class Device
    {
        public ProfileService Profiles = null!;
        public ProfileConfigStore Configs = null!;
        public ProfileSyncEngine Engine = null!;
        public string Folder = "";
    }

    public SyncEngineTests() => _shared = new FolderProfileStore(_dir.Sub("shared"));
    public void Dispose() => _dir.Dispose();

    Device NewDevice(string name)
    {
        var d = new Device { Folder = _dir.Sub(name) };
        Directory.CreateDirectory(d.Folder);
        d.Profiles = new ProfileService(Path.Combine(d.Folder, "profiles.json"));
        d.Configs = new ProfileConfigStore(Path.Combine(d.Folder, "configs"));
        d.Engine = new ProfileSyncEngine(d.Profiles, d.Configs, name);
        return d;
    }

    static InstalledMod Mod(string id, string version = "1.0.0", bool enabled = true)
        => new(new ModManifest { UniqueID = id, Name = id, Version = version }, "/m/" + id, enabled);

    async Task<SyncReport> SyncAsync(Device d, SyncDirection dir = SyncDirection.Both)
        => await d.Engine.ApplyAsync(await d.Engine.CheckAsync(_shared), _shared, dir);

    [Fact]
    public async Task ANewProfileGoesUpAndComesDownOnAnotherDevice()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("Supervivencia", new[] { Mod("x.y") });
        Assert.Equal(1, a.Engine.PendingLocalChanges());

        var up = await SyncAsync(a);
        Assert.Equal(new[] { p.Id }, up.Pushed);
        Assert.Equal(0, a.Engine.PendingLocalChanges());

        var down = await SyncAsync(b);
        Assert.Equal(new[] { p.Id }, down.Pulled);
        Assert.Equal("Supervivencia", b.Profiles.Active.Single().Name);
        Assert.Equal("x.y", b.Profiles.Find(p.Id)!.Mods.Single().UniqueID);

        var again = await SyncAsync(b);                                   // nothing left to do
        Assert.Empty(again.Pulled); Assert.Empty(again.Pushed); Assert.Empty(again.Conflicts);
    }

    [Fact]
    public async Task AChangeOnOneDeviceReachesTheOtherWithoutAConflict()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("P", new[] { Mod("x.y") });
        await SyncAsync(a); await SyncAsync(b);

        var pb = b.Profiles.Find(p.Id)!;
        b.Profiles.UpsertMod(pb, Mod("x.y"), false);                      // B turns the mod off
        var upB = await SyncAsync(b);
        Assert.Equal(new[] { p.Id }, upB.Pushed);

        var downA = await SyncAsync(a);
        Assert.Equal(new[] { p.Id }, downA.Pulled);
        Assert.False(a.Profiles.Find(p.Id)!.Mods[0].Enabled);
    }

    [Fact]
    public async Task ChangingTheSameProfileOnTwoDevicesIsAConflictAndNothingIsOverwritten()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("P", new[] { Mod("x.y") });
        await SyncAsync(a); await SyncAsync(b);

        a.Profiles.UpsertMod(a.Profiles.Find(p.Id)!, Mod("only.a"), true);
        b.Profiles.UpsertMod(b.Profiles.Find(p.Id)!, Mod("only.b"), true);
        await SyncAsync(a);                                               // A uploads first

        var plan = await b.Engine.CheckAsync(_shared);
        Assert.Equal(SyncState.Conflict, plan.Find(p.Id)!.State);
        var report = await b.Engine.ApplyAsync(plan, _shared);
        Assert.Equal(new[] { p.Id }, report.Conflicts);
        Assert.Contains(b.Profiles.Find(p.Id)!.Mods, m => m.UniqueID == "only.b");        // B's work is still there
        Assert.DoesNotContain(b.Profiles.Find(p.Id)!.Mods, m => m.UniqueID == "only.a");
    }

    [Fact]
    public async Task KeepingTheRemoteVersionSavesTheLocalOneAsACopy()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("P", new[] { Mod("x.y") });
        await SyncAsync(a); await SyncAsync(b);
        a.Profiles.UpsertMod(a.Profiles.Find(p.Id)!, Mod("only.a"), true); await SyncAsync(a);
        b.Profiles.UpsertMod(b.Profiles.Find(p.Id)!, Mod("only.b"), true);

        var copyId = await b.Engine.ResolveAsync(p.Id, ConflictChoice.KeepRemote, _shared);
        Assert.NotNull(copyId);
        Assert.Contains(b.Profiles.Find(p.Id)!.Mods, m => m.UniqueID == "only.a");        // the original is now A's version
        var copy = b.Profiles.Find(copyId!)!;
        Assert.Equal("P (local)", copy.Name);
        Assert.Contains(copy.Mods, m => m.UniqueID == "only.b");                          // and B's version was kept as a copy

        var after = await b.Engine.CheckAsync(_shared);
        Assert.Equal(SyncState.UpToDate, after.Find(p.Id)!.State);
    }

    [Fact]
    public async Task KeepingTheLocalVersionReplacesTheRemoteOne()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("P", new[] { Mod("x.y") });
        await SyncAsync(a); await SyncAsync(b);
        a.Profiles.UpsertMod(a.Profiles.Find(p.Id)!, Mod("only.a"), true); await SyncAsync(a);
        b.Profiles.UpsertMod(b.Profiles.Find(p.Id)!, Mod("only.b"), true);

        Assert.Null(await b.Engine.ResolveAsync(p.Id, ConflictChoice.KeepLocal, _shared));
        await SyncAsync(a);                                                                // A now receives B's version
        Assert.Contains(a.Profiles.Find(p.Id)!.Mods, m => m.UniqueID == "only.b");
        Assert.DoesNotContain(a.Profiles.Find(p.Id)!.Mods, m => m.UniqueID == "only.a");
    }

    [Fact]
    public async Task TheSameEditOnBothDevicesIsNotAConflict()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var p = a.Profiles.Create("P", new[] { Mod("x.y") });
        await SyncAsync(a); await SyncAsync(b);
        a.Profiles.UpsertMod(a.Profiles.Find(p.Id)!, Mod("x.y"), false);
        b.Profiles.UpsertMod(b.Profiles.Find(p.Id)!, Mod("x.y"), false);                   // same result, different revisions
        await SyncAsync(a);
        var plan = await b.Engine.CheckAsync(_shared);
        Assert.Equal(SyncState.UpToDate, plan.Find(p.Id)!.State);
    }

    [Fact]
    public async Task ADeletionTravelsAndAProfileSwitchedOffStaysLocal()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var keep = a.Profiles.Create("Keep", new[] { Mod("k.k") });
        var secret = a.Profiles.Create("Mine only", new[] { Mod("s.s") });
        secret.SyncEnabled = false;
        await SyncAsync(a); await SyncAsync(b);
        Assert.Single(b.Profiles.Active);                                                  // the switched-off one never left A
        Assert.Equal(SyncState.LocalOnly, (await a.Engine.CheckAsync(_shared)).Find(secret.Id)!.State);

        a.Profiles.Delete(a.Profiles.Find(keep.Id)!);
        await SyncAsync(a);
        await SyncAsync(b);
        Assert.Empty(b.Profiles.Active);
    }

    [Fact]
    public async Task ModSettingsTravelWithTheProfile()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        var mod = Path.Combine(a.Folder, "Mods", "x.y");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "config.json"), "{\"Level\":7}");
        var installed = new[] { new InstalledMod(new ModManifest { UniqueID = "x.y", Name = "x.y" }, mod, true) };
        var p = a.Profiles.Create("P", installed);
        a.Configs.Capture(p, installed);
        a.Profiles.MarkChanged(p);
        await SyncAsync(a); await SyncAsync(b);
        Assert.Equal("{\"Level\":7}", b.Configs.ReadAll(b.Profiles.Find(p.Id)!)["x.y"]);
    }

    [Fact]
    public async Task ProfilesFromTheOldSingleFileAreImportedAndThenUpgraded()
    {
        var legacy = new ProfilesDocument { Profiles = { new Profile { Id = "old1", Name = "Old profile", Mods = { new ProfileMod { UniqueID = "o.o", Name = "o", Version = "1.0.0" } } } } };
        Directory.CreateDirectory(_dir.Sub("shared"));
        File.WriteAllText(Path.Combine(_dir.Sub("shared"), "sdvmods-profiles.json"), legacy.ToJson());

        var b = NewDevice("b");
        var down = await SyncAsync(b);
        Assert.Equal(new[] { "old1" }, down.Pulled);
        Assert.Equal("Old profile", b.Profiles.Active.Single().Name);

        b.Profiles.Rename(b.Profiles.Find("old1")!, "Renamed");                            // first edit under the new format goes up as its own file
        await SyncAsync(b);
        Assert.True(File.Exists(Path.Combine(_dir.Sub("shared"), "junimo-profile-old1.json")));
        var a = NewDevice("a");
        await SyncAsync(a);
        Assert.Equal("Renamed", a.Profiles.Active.Single().Name);                          // the new file wins over the legacy entry
    }

    [Fact]
    public async Task PushOnlyAndPullOnlyDoNotCrossOver()
    {
        var a = NewDevice("a"); var b = NewDevice("b");
        a.Profiles.Create("FromA", new[] { Mod("a.a") });
        b.Profiles.Create("FromB", new[] { Mod("b.b") });
        var pushed = await SyncAsync(a, SyncDirection.PushOnly);
        Assert.Single(pushed.Pushed); Assert.Empty(pushed.Pulled);
        var pulled = await SyncAsync(b, SyncDirection.PullOnly);
        Assert.Single(pulled.Pulled); Assert.Empty(pulled.Pushed);
        Assert.Equal(2, b.Profiles.Active.Count());
        Assert.Equal(1, b.Engine.PendingLocalChanges());                                   // FromB still waiting to go up
    }
}
