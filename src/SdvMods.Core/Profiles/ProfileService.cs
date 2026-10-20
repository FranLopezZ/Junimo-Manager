using SdvMods.Core.Mods;

namespace SdvMods.Core.Profiles;

public sealed record ProfilePlan(
    List<ProfileMod> ToInstall,
    List<(InstalledMod Mod, bool Enable)> ToToggle,
    List<(ProfileMod Wanted, InstalledMod Have)> Outdated);

/// <summary>Local profile storage plus the logic to capture the current mods into a profile and to apply a profile to the Mods folder.</summary>
public sealed class ProfileService
{
    readonly string _file;
    public ProfilesDocument Document { get; private set; } = new();

    public ProfileService(string file)
    {
        _file = file;
        if (File.Exists(file))
        {
            try { Document = ProfilesDocument.FromJson(File.ReadAllText(file)); } catch { Document = new(); }
        }
    }

    public IEnumerable<Profile> Active => Document.Profiles.Where(p => !p.Deleted).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);

    public Profile? Find(string? id) => Document.Profiles.FirstOrDefault(p => p.Id == id && !p.Deleted);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, Document.ToJson());
        File.Move(tmp, _file, overwrite: true);
    }

    public void Replace(ProfilesDocument doc) { Document = doc; Save(); }

    public Profile Create(string name, IEnumerable<InstalledMod>? from = null)
    {
        var p = new Profile { Name = name };
        if (from is not null) Capture(p, from);
        Document.Profiles.Add(p);
        Save();
        return p;
    }

    public Profile Add(Profile p) { Document.Profiles.Add(p); Save(); return p; }

    public Profile Duplicate(Profile source, string name)
    {
        var p = new Profile
        {
            Name = name,
            Mods = source.Mods.Select(m => new ProfileMod { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = m.Enabled, UpdateKeys = m.UpdateKeys.ToList() }).ToList(),
        };
        Document.Profiles.Add(p);
        Save();
        return p;
    }

    public void Rename(Profile p, string name) { p.Name = name; Touch(p); }

    public void Delete(Profile p) { p.Deleted = true; p.Mods.Clear(); Touch(p); }

    void Touch(Profile p) { p.UpdatedAt = DateTimeOffset.UtcNow; p.Rev = Guid.NewGuid().ToString("N"); Save(); }

    /// <summary>Marks the profile as modified (new revision) for something that is not in its mod list, e.g. its stored mod settings.</summary>
    public void MarkChanged(Profile p) => Touch(p);

    /// <summary>Record the mods currently on disk (and their enabled state) into the profile.</summary>
    /// <param name="keepPending">IDs of profile mods that are still waiting to be installed on this device; they stay in the profile.</param>
    public void Capture(Profile p, IEnumerable<InstalledMod> installed, ISet<string>? keepPending = null)
    {
        var fromDisk = installed.Where(m => !ModScanner.IsBundled(m))
            .GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(m => m.Enabled).First())
            .Select(m => new ProfileMod { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = m.Enabled, UpdateKeys = m.Manifest.UpdateKeys.ToList() })
            .ToList();
        if (keepPending is { Count: > 0 })
            fromDisk.AddRange(p.Mods.Where(m => keepPending.Contains(m.UniqueID) && !fromDisk.Any(d => string.Equals(d.UniqueID, m.UniqueID, StringComparison.OrdinalIgnoreCase))));
        p.Mods = fromDisk.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Touch(p);
    }

    public Profile CreateEmpty(string name)
    {
        var p = new Profile { Name = name };
        Document.Profiles.Add(p);
        Save();
        return p;
    }

    static ProfileMod ToProfileMod(InstalledMod m, bool enabled)
        => new() { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = enabled, UpdateKeys = m.Manifest.UpdateKeys.ToList() };

    /// <summary>Adds the mod to the profile (or updates its entry).</summary>
    public void UpsertMod(Profile p, InstalledMod m, bool enabled)
    {
        var i = p.Mods.FindIndex(x => string.Equals(x.UniqueID, m.UniqueID, StringComparison.OrdinalIgnoreCase));
        var entry = ToProfileMod(m, enabled);
        if (i >= 0) p.Mods[i] = entry; else p.Mods.Add(entry);
        p.Mods = p.Mods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Touch(p);
    }

    /// <summary>Takes the mod out of the profile (the files stay on disk, available to other profiles).</summary>
    public bool RemoveMod(Profile p, string uniqueId)
    {
        var n = p.Mods.RemoveAll(x => string.Equals(x.UniqueID, uniqueId, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Touch(p);
        return n > 0;
    }

    public static bool IsMember(Profile p, string uniqueId)
        => p.Mods.Any(x => string.Equals(x.UniqueID, uniqueId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Brings the entries of mods that are in the profile in line with the disk (name, version, enabled, update keys).
    /// Mods on disk that are not members are left alone, and members not on disk stay (they are waiting to be installed).
    /// </summary>
    public bool RefreshMembers(Profile p, IEnumerable<InstalledMod> installed)
    {
        var byId = installed.Where(m => !ModScanner.IsBundled(m))
            .GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Enabled).First(), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        for (var i = 0; i < p.Mods.Count; i++)
        {
            if (!byId.TryGetValue(p.Mods[i].UniqueID, out var disk)) continue;
            var fresh = ToProfileMod(disk, disk.Enabled);
            var old = p.Mods[i];
            if (old.Name != fresh.Name || old.Version != fresh.Version || old.Enabled != fresh.Enabled || !old.UpdateKeys.SequenceEqual(fresh.UpdateKeys))
            {
                p.Mods[i] = fresh;
                changed = true;
            }
        }
        if (changed) Touch(p);
        return changed;
    }

    public static ProfilePlan Plan(Profile p, IReadOnlyCollection<InstalledMod> installed)
    {
        var byId = installed.Where(m => !ModScanner.IsBundled(m))
            .GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var wanted = p.Mods.ToDictionary(m => m.UniqueID, StringComparer.OrdinalIgnoreCase);

        var toInstall = new List<ProfileMod>();
        var toggle = new List<(InstalledMod, bool)>();
        var outdated = new List<(ProfileMod, InstalledMod)>();

        foreach (var w in p.Mods)
        {
            if (!byId.TryGetValue(w.UniqueID, out var have)) { toInstall.Add(w); continue; }
            if (have.Enabled != w.Enabled) toggle.Add((have, w.Enabled));
            if (SemVer.IsNewer(w.Version, have.Version)) outdated.Add((w, have));
        }
        // Installed but not part of this profile: keep on disk, but make sure it does not load.
        foreach (var have in byId.Values.Where(m => !wanted.ContainsKey(m.UniqueID) && m.Enabled))
            toggle.Add((have, false));
        return new ProfilePlan(toInstall, toggle, outdated);
    }

    /// <summary>Enable/disable mods to match the profile. Returns the number of folders renamed.</summary>
    public static int ApplyToggles(string modsFolder, ProfilePlan plan)
    {
        var n = 0;
        foreach (var (mod, enable) in plan.ToToggle) { ModScanner.SetEnabled(modsFolder, mod, enable); n++; }
        return n;
    }
}
