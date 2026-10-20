namespace SdvMods.Core.Mods;

/// <summary>
/// Lists the mods in the game's Mods folder the same way SMAPI does: any folder containing a manifest.json
/// is a mod, other folders are scanned deeper, and folders whose name starts with '.' are ignored by SMAPI
/// (this is how the manager disables a mod).
/// </summary>
public static class ModScanner
{
    public const string DisabledPrefix = ".";

    public static List<InstalledMod> Scan(string modsFolder)
    {
        var result = new List<InstalledMod>();
        if (Directory.Exists(modsFolder)) ScanDir(modsFolder, modsFolder, result);
        return result;
    }

    static void ScanDir(string root, string dir, List<InstalledMod> result)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var leaf = Path.GetFileName(sub);
            if (leaf.StartsWith(".sdvmods-", StringComparison.Ordinal)) continue; // our own staging folders
            var manifestPath = Path.Combine(sub, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var manifest = ModManifest.Load(manifestPath);
                if (manifest is not null) result.Add(new InstalledMod(manifest, sub, IsEnabledPath(root, sub)));
            }
            else ScanDir(root, sub, result);
        }
    }

    public static bool IsEnabledPath(string modsRoot, string folder)
    {
        var rel = Path.GetRelativePath(modsRoot, folder);
        return !rel.Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith(DisabledPrefix, StringComparison.Ordinal));
    }

    /// <summary>SMAPI's own bundled mods (ConsoleCommands, SaveBackup...). They are never managed through profiles.</summary>
    public static bool IsBundled(InstalledMod m) => m.UniqueID.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enable or disable a mod by (un)prefixing its folder names with '.'. Returns the new folder path.</summary>
    public static string SetEnabled(string modsRoot, InstalledMod mod, bool enabled)
    {
        var rel = Path.GetRelativePath(modsRoot, mod.Folder).Split(Path.DirectorySeparatorChar);
        var current = modsRoot;
        for (var i = 0; i < rel.Length; i++)
        {
            var seg = rel[i];
            var isLeaf = i == rel.Length - 1;
            var wantDot = !enabled && isLeaf;
            var hasDot = seg.StartsWith(DisabledPrefix, StringComparison.Ordinal);
            var newSeg = seg;
            if (enabled && hasDot) newSeg = seg.TrimStart('.');
            else if (wantDot && !hasDot) newSeg = DisabledPrefix + seg;
            if (newSeg != seg)
            {
                var from = Path.Combine(current, seg);
                var to = Path.Combine(current, newSeg);
                if (Directory.Exists(to)) throw new IOException($"Cannot rename '{seg}': '{newSeg}' already exists.");
                MoveWithRetry(from, to);
            }
            current = Path.Combine(current, newSeg);
        }
        return current;
    }

    /// <summary>On Windows a folder cannot be renamed while something (an antivirus, the indexer, a copy in progress) has a file inside open; that is usually over within a moment.</summary>
    static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (attempt < 6 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    public sealed record MissingDependency(InstalledMod Mod, string DependencyId, string? MinimumVersion, bool Disabled);

    /// <summary>Required dependencies (incl. content-pack parents) that are missing, disabled or too old.</summary>
    public static List<MissingDependency> FindMissingDependencies(IReadOnlyCollection<InstalledMod> mods)
    {
        var byId = mods.GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Enabled).First(), StringComparer.OrdinalIgnoreCase);
        var list = new List<MissingDependency>();
        foreach (var m in mods.Where(x => x.Enabled))
        {
            var deps = m.Manifest.Dependencies.Where(d => d.IsRequired).Select(d => (d.UniqueID, d.MinimumVersion)).ToList();
            if (m.Manifest.ContentPackFor is { } cp) deps.Add((cp.UniqueID, cp.MinimumVersion));
            foreach (var (id, min) in deps)
            {
                if (id.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase)) continue;
                if (!byId.TryGetValue(id, out var found)) list.Add(new(m, id, min, false));
                else if (!found.Enabled) list.Add(new(m, id, min, true));
                else if (min is not null && SemVer.TryParse(min, out var need) && SemVer.TryParse(found.Version, out var have) && have.CompareTo(need) < 0)
                    list.Add(new(m, id, min, false));
            }
        }
        return list;
    }

    static IEnumerable<string> RequiredIds(InstalledMod m)
    {
        foreach (var d in m.Manifest.Dependencies.Where(d => d.IsRequired)) yield return d.UniqueID;
        if (m.Manifest.ContentPackFor is { } cp) yield return cp.UniqueID;
    }

    /// <summary>
    /// Enabled mods that need any of <paramref name="targetIds"/> (directly or through each other), so they stop working when the targets are
    /// turned off or removed. The targets themselves are not included.
    /// </summary>
    public static List<InstalledMod> FindDependants(IReadOnlyCollection<InstalledMod> all, IEnumerable<string> targetIds)
    {
        var gone = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledMod>();
        bool grew;
        do
        {
            grew = false;
            foreach (var m in all)
            {
                if (!m.Enabled || IsBundled(m) || gone.Contains(m.UniqueID)) continue;
                if (RequiredIds(m).Any(gone.Contains))
                {
                    result.Add(m);
                    gone.Add(m.UniqueID);
                    grew = true;
                }
            }
        } while (grew);
        return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Installed but disabled mods that <paramref name="targetIds"/> need (directly or through each other), so they would not work when turned on.</summary>
    public static List<InstalledMod> FindDisabledRequirements(IReadOnlyCollection<InstalledMod> all, IEnumerable<string> targetIds)
    {
        var byId = all.Where(m => !IsBundled(m)).GroupBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Enabled).First(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(targetIds, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(seen);
        var result = new List<InstalledMod>();
        while (queue.Count > 0)
        {
            if (!byId.TryGetValue(queue.Dequeue(), out var mod)) continue;
            foreach (var id in RequiredIds(mod))
            {
                if (!seen.Add(id) || !byId.TryGetValue(id, out var need)) continue;
                if (!need.Enabled) result.Add(need);
                queue.Enqueue(id);
            }
        }
        return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
