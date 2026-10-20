using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SdvMods.Core.Localization;
using SdvMods.Core.Settings;
using SdvMods.Core.Updates;

namespace SdvMods.App.Services;

/// <summary>
/// In-place update from the project's GitHub release: download the package for this OS, unpack it, swap the files and
/// relaunch. A running executable cannot be overwritten on Windows but it CAN be renamed, and on Linux it can be
/// replaced by renaming a new file over it, so no helper process is needed.
/// </summary>
public static class AppUpdater
{
    public static bool CanSelfUpdate => NxmRegistration.CanRegister;

    static string AppDir => Path.GetDirectoryName(Environment.ProcessPath!)!;

    /// <summary>Downloads and unpacks the release; returns the folder with the new files.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, AppRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.AssetUrl is null) throw new InvalidOperationException(Loc.T("La versión publicada no incluye un paquete para este sistema."));
        var work = Path.Combine(AppPaths.CacheDir, "update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var file = Path.Combine(work, Path.GetFileName(new Uri(release.AssetUrl).AbsolutePath));
        await Http.DownloadAsync(http, release.AssetUrl, file, progress, ct);

        var dir = Path.Combine(work, "new");
        await Task.Run(() => PackageExtractor.Extract(file, dir), ct);

        var exeName = Path.GetFileName(Environment.ProcessPath!);
        var folder = PackageExtractor.FindProgramFolder(dir, exeName)
                     ?? throw new InvalidDataException(Loc.T("El paquete descargado no contiene el programa."));
        File.Delete(file);
        return folder;
    }

    /// <summary>Swaps the files and starts the new version. The caller must then close this instance.</summary>
    public static void ApplyAndRestart(string newDir)
    {
        var exe = Environment.ProcessPath!;
        var exeName = Path.GetFileName(exe);
        foreach (var src in Directory.GetFiles(newDir))
        {
            var name = Path.GetFileName(src);
            if (name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) continue; // the installer is only for first installs
            FileSwap.Replace(src, Path.Combine(AppDir, name), isExe: name == exeName);
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppDir };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>Starts a fresh copy of the app (the caller then closes this one).</summary>
    public static void Relaunch()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppDir };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>Called at startup: waits for the previous instance to close and removes leftovers of the last update.</summary>
    public static void HandleStartup(ref string[] args)
    {
        if (args.Length >= 2 && args[0] == "--after-update")
        {
            if (int.TryParse(args[1], out var pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(20000); } catch { /* already gone */ }
            }
            args = args[2..];
        }
        try
        {
            foreach (var f in Directory.GetFiles(AppDir, "*.old")) try { File.Delete(f); } catch { }
        }
        catch { /* best effort */ }
    }
}
