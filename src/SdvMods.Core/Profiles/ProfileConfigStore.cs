using SdvMods.Core.Mods;

namespace SdvMods.Core.Profiles;

/// <summary>
/// Keeps a copy of each mod's config.json per profile. Mods themselves live once in the game's Mods folder (shared by every
/// profile), so a profile's settings are stored aside and written back into the mods when the profile is activated:
/// "Capture" copies the files on disk into the active profile's store, "Apply" puts a profile's stored files on disk.
/// </summary>
public sealed class ProfileConfigStore
{
    public const long MaxFileBytes = 1_000_000;
    readonly string _root;

    public ProfileConfigStore(string root) => _root = root;

    public string DirFor(string profileId) => Path.Combine(_root, Sanitize(profileId));

    static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }

    string FileFor(string profileId, string modId) => Path.Combine(DirFor(profileId), Sanitize(modId) + ".json");

    static IEnumerable<InstalledMod> OnDisk(Profile profile, IEnumerable<InstalledMod> installed)
    {
        var members = profile.Mods.Select(m => m.UniqueID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.Where(m => !ModScanner.IsBundled(m) && members.Contains(m.UniqueID))
            .GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(m => m.Enabled).First());
    }

    static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Copies the config.json of every mod in the profile from disk into the profile's store (mirrors the disk). Returns how many changed.</summary>
    public int Capture(Profile profile, IEnumerable<InstalledMod> installed)
    {
        var changed = 0;
        foreach (var mod in OnDisk(profile, installed))
        {
            var src = ModConfigDocument.ConfigPath(mod);
            var dst = FileFor(profile.Id, mod.UniqueID);
            if (!File.Exists(src))
            {
                if (File.Exists(dst)) { File.Delete(dst); changed++; }   // the config was deleted (defaults restored)
                continue;
            }
            if (new FileInfo(src).Length > MaxFileBytes) continue;
            if (File.Exists(dst) && SameBytes(src, dst)) continue;
            Directory.CreateDirectory(DirFor(profile.Id));
            File.Copy(src, dst, overwrite: true);
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Writes the profile's stored configs into the mods. A mod that has no stored config yet keeps its current file, which the profile
    /// adopts (so nothing is ever lost). Returns how many files were written.
    /// </summary>
    public int Apply(Profile profile, IEnumerable<InstalledMod> installed) => Apply(profile, installed, out _);

    /// <param name="adopted">How many mods had nothing stored and had their current file taken into the profile.</param>
    public int Apply(Profile profile, IEnumerable<InstalledMod> installed, out int adopted)
    {
        var written = 0;
        foreach (var mod in OnDisk(profile, installed))
        {
            var src = FileFor(profile.Id, mod.UniqueID);
            var dst = ModConfigDocument.ConfigPath(mod);
            if (!File.Exists(src)) continue;
            if (File.Exists(dst) && SameBytes(src, dst)) continue;
            File.Copy(src, dst, overwrite: true);
            written++;
        }
        adopted = Capture(profile, installed); // adopt the current file of mods the profile had nothing stored for
        return written;
    }

    public void Copy(string fromProfileId, string toProfileId)
    {
        var from = DirFor(fromProfileId);
        if (!Directory.Exists(from)) return;
        var to = DirFor(toProfileId);
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>Replaces the whole store of a profile (used when a profile is pulled from another device).</summary>
    public void Replace(string profileId, IReadOnlyDictionary<string, string> configs)
    {
        Delete(profileId);
        WriteAll(profileId, configs);
    }

    public void Delete(string profileId)
    {
        var dir = DirFor(profileId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>The stored configs by mod id (used when exporting a profile with its settings).</summary>
    public Dictionary<string, string> ReadAll(Profile profile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in profile.Mods)
        {
            var f = FileFor(profile.Id, m.UniqueID);
            if (File.Exists(f)) result[m.UniqueID] = File.ReadAllText(f);
        }
        return result;
    }

    /// <summary>Stores configs that came with an imported profile.</summary>
    public void WriteAll(string profileId, IReadOnlyDictionary<string, string> configs)
    {
        if (configs.Count == 0) return;
        Directory.CreateDirectory(DirFor(profileId));
        foreach (var (modId, text) in configs) File.WriteAllText(FileFor(profileId, modId), text);
    }

    public bool Has(string profileId, string modId) => File.Exists(FileFor(profileId, modId));
}
