using SdvMods.Core.Localization;
using System.Diagnostics;
using System.Text.Json;
using SdvMods.Core.Mods;
using SdvMods.Core.Updates;

namespace SdvMods.Core.Game;

public sealed record SmapiRelease(string Version, string FileName, string Url);

/// <summary>
/// Downloads the official SMAPI installer package and runs it in a visible terminal window. The official installer is
/// interactive (it needs a real console) and knows every platform-specific detail, e.g. replacing the Linux game
/// launcher so Steam / the Steam Deck start SMAPI automatically, so we run it instead of re-implementing it.
/// </summary>
public sealed class SmapiInstaller
{
    readonly HttpClient _http;
    public SmapiInstaller(HttpClient http) => _http = http;

    public async Task<SmapiRelease?> LatestAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("-installer.zip", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("developers", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("double", StringComparison.OrdinalIgnoreCase))
                return new SmapiRelease(tag, name, a.GetProperty("browser_download_url").GetString()!);
        }
        return null;
    }

    /// <summary>Downloads and unpacks the installer; returns the folder that contains "internal/".</summary>
    public async Task<string> PrepareAsync(SmapiRelease release, string cacheDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var zip = Path.Combine(cacheDir, release.FileName);
        await Http.DownloadAsync(_http, release.Url, zip, progress, ct);
        var target = Path.Combine(cacheDir, "smapi-installer-" + release.Version);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ModInstaller.ExtractSafely(zip, target);
        File.Delete(zip);

        var dir = Directory.EnumerateDirectories(target, "internal", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException(Loc.T("El paquete de SMAPI no tiene el formato esperado."));
        return Path.GetDirectoryName(dir)!;
    }

    /// <summary>Runs the official installer ("--install") in its own console window and waits until it closes.</summary>
    public static async Task RunAsync(string installerDir, CancellationToken ct = default)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(Path.Combine(installerDir, "internal", "windows", "SMAPI.Installer.exe"), "--install")
            { UseShellExecute = true, WorkingDirectory = installerDir };
        }
        else
        {
            var exe = Path.Combine(installerDir, "internal", "linux", "SMAPI.Installer");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            var terminal = FindTerminal(exe)
                ?? throw new InvalidOperationException(Loc.T("No se encontró un terminal (konsole, gnome-terminal, xterm…). Ejecuta «install on Linux.sh» a mano en: ") + installerDir);
            psi = new ProcessStartInfo(terminal.File) { UseShellExecute = false, WorkingDirectory = installerDir };
            foreach (var a in terminal.Args) psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("No se pudo iniciar el instalador de SMAPI."));
        await p.WaitForExitAsync(ct);
    }

    static (string File, string[] Args)? FindTerminal(string installer)
    {
        (string Name, string[] Prefix)[] options =
        {
            ("konsole", new[] { "--nofork", "-e" }),            // SteamOS desktop mode
            ("gnome-terminal", new[] { "--wait", "--" }),
            ("alacritty", new[] { "-e" }),
            ("xterm", new[] { "-e" }),
            ("x-terminal-emulator", new[] { "-e" }),
        };
        foreach (var (name, prefix) in options)
            if (OnPath(name)) return (name, prefix.Concat(new[] { installer, "--install" }).ToArray());
        return null;
    }

    static bool OnPath(string exe)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));
}

public static class GameLauncher
{
    /// <summary>Windows: start SMAPI directly. Linux/Deck: ask Steam to start the game (SMAPI's installer replaced its launcher).</summary>
    public static void Play(string gamePath)
    {
        if (OperatingSystem.IsWindows())
        {
            var exe = Path.Combine(gamePath, "StardewModdingAPI.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException(Loc.T("SMAPI no está instalado."), exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = gamePath });
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", "steam://rungameid/413150") { UseShellExecute = false });
        }
    }
}
