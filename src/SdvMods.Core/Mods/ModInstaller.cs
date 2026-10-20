using SdvMods.Core.Localization;
using System.Text;
using SharpCompress.Archives;

namespace SdvMods.Core.Mods;

public sealed record InstallResult(List<InstalledMod> Installed, List<string> Warnings);

/// <summary>
/// Installs mods from .zip/.7z/.rar archives into the Mods folder the way SMAPI expects: every folder that
/// contains a manifest.json becomes one mod folder, wrapper folders are flattened, updates replace the old
/// copy (which is backed up first), and the user's config.json and enabled/disabled state are preserved.
/// </summary>
public sealed class ModInstaller
{
    readonly string _mods;
    readonly string _backups;
    const int KeepBackups = 3;

    public ModInstaller(string modsFolder, string backupsFolder)
    {
        _mods = modsFolder;
        _backups = backupsFolder;
    }

    /// <param name="expectedUniqueId">When set, refuse archives that do not contain this mod (guards against wrong release assets).</param>
    public InstallResult InstallArchive(string archivePath, string? expectedUniqueId = null)
    {
        Directory.CreateDirectory(_mods);
        var staging = Path.Combine(_mods, ".sdvmods-staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        var warnings = new List<string>();
        var installed = new List<InstalledMod>();
        try
        {
            ExtractSafely(archivePath, staging);
            var roots = FindModRoots(staging);
            if (roots.Count == 0)
                throw new InvalidDataException(Loc.T("El archivo no contiene ningún mod (no hay manifest.json)."));

            if (expectedUniqueId is not null && !roots.Any(r =>
                    string.Equals(ModManifest.Load(Path.Combine(r, "manifest.json"))?.UniqueID, expectedUniqueId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(Loc.F("El archivo descargado no contiene el mod esperado ({0}).", expectedUniqueId));

            var existing = ModScanner.Scan(_mods);
            foreach (var root in roots)
            {
                var manifest = ModManifest.Load(Path.Combine(root, "manifest.json"))!;
                installed.Add(Place(root, staging, manifest, existing, warnings));
            }
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }

        foreach (var d in ModScanner.FindMissingDependencies(ModScanner.Scan(_mods)).Where(d => installed.Any(i => i.UniqueID == d.Mod.UniqueID)))
            warnings.Add(d.Disabled
                ? Loc.F("«{0}» necesita «{1}» (está desactivado).", d.Mod.Name, d.DependencyId)
                : Loc.F("«{0}» necesita «{1}» (no instalado o versión antigua).", d.Mod.Name, d.DependencyId));
        return new InstallResult(installed, warnings);
    }

    internal static void ExtractSafely(string archivePath, string destination)
    {
        var destFull = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null) continue;
            var key = entry.Key.Replace('\\', '/').TrimStart('/');
            if (key.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, key));
            if (!target.StartsWith(destFull, StringComparison.Ordinal))
                throw new InvalidDataException(Loc.F("Entrada de archivo no segura: {0}", entry.Key));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.OpenEntryStream();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    /// <summary>Top-most folders that contain a manifest.json (a mod never contains another mod).</summary>
    static List<string> FindModRoots(string staging)
    {
        var roots = new List<string>();
        void Walk(string dir)
        {
            if (File.Exists(Path.Combine(dir, "manifest.json")) && ModManifest.Load(Path.Combine(dir, "manifest.json")) is not null)
            {
                roots.Add(dir);
                return;
            }
            foreach (var sub in Directory.EnumerateDirectories(dir)) Walk(sub);
        }
        Walk(staging);
        return roots;
    }

    InstalledMod Place(string sourceRoot, string staging, ModManifest manifest, List<InstalledMod> existing, List<string> warnings)
    {
        var old = existing.FirstOrDefault(m => string.Equals(m.UniqueID, manifest.UniqueID, StringComparison.OrdinalIgnoreCase));
        string target;
        if (old is not null)
        {
            target = old.Folder;
            Backup(old);
            // Carry over the user's settings.
            var oldConfig = Path.Combine(old.Folder, "config.json");
            if (File.Exists(oldConfig)) File.Copy(oldConfig, Path.Combine(sourceRoot, "config.json"), overwrite: true);
            Directory.Delete(old.Folder, true);
        }
        else
        {
            var name = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar)
                ? Sanitize(manifest.Name)
                : Path.GetFileName(sourceRoot);
            target = Unique(Path.Combine(_mods, name.TrimStart('.')));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MoveDirectory(sourceRoot, target);
        if (old is not null) warnings.Add($"'{manifest.Name}' actualizado: {old.Version} → {manifest.Version}");
        return new InstalledMod(ModManifest.Load(Path.Combine(target, "manifest.json"))!, target, old?.Enabled ?? true);
    }

    void Backup(InstalledMod old)
    {
        var dir = Path.Combine(_backups, Sanitize(old.UniqueID));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{Sanitize(old.Version)}_{DateTime.Now:yyyyMMdd-HHmmss}");
        CopyDirectory(old.Folder, dest);
        foreach (var stale in Directory.EnumerateDirectories(dir).OrderByDescending(Directory.GetCreationTimeUtc).Skip(KeepBackups))
            try { Directory.Delete(stale, true); } catch { }
    }

    /// <summary>Most recent backup of a mod, for rollback.</summary>
    public string? LatestBackup(string uniqueId)
    {
        var dir = Path.Combine(_backups, Sanitize(uniqueId));
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault() : null;
    }

    public void Uninstall(InstalledMod mod) => Directory.Delete(mod.Folder, true);

    static string Unique(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (var i = 2; ; i++) if (!Directory.Exists($"{path} ({i})")) return $"{path} ({i})";
    }

    static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "mod" : s;
    }

    static void MoveDirectory(string from, string to)
    {
        try { Directory.Move(from, to); }
        catch (IOException) { CopyDirectory(from, to); Directory.Delete(from, true); }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
