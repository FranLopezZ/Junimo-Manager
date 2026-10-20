using System.Diagnostics;
using SdvMods.Core.Localization;
using System.Reflection;

namespace SdvMods.Core.Game;

public sealed record SmapiStatus(bool Installed, string? InstalledVersion, string? LatestVersion, string? Problem)
{
    public bool UpdateAvailable => Installed && InstalledVersion is not null && LatestVersion is not null
        && SdvMods.Core.Mods.SemVer.IsNewer(LatestVersion.TrimStart('v'), InstalledVersion);
    public bool Healthy => Installed && InstalledVersion is not null && Problem is null;
}

public static class SmapiInfo
{
    /// <summary>Reads the installed SMAPI version from its own assembly; null if not installed or unreadable.</summary>
    public static string? InstalledVersion(string gamePath)
    {
        foreach (var n in new[] { "StardewModdingAPI.dll", "StardewModdingAPI.exe" })
        {
            var p = Path.Combine(gamePath, n);
            if (!File.Exists(p)) continue;
            try
            {
                var pv = FileVersionInfo.GetVersionInfo(p).ProductVersion;
                if (!string.IsNullOrWhiteSpace(pv)) return pv.Split('+', '-')[0].Trim();
                var v = AssemblyName.GetAssemblyName(p).Version;
                if (v is not null) return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
            }
            catch { }
        }
        return null;
    }

    /// <summary>Checks the installation and, when a release is given, compares it to the latest one.</summary>
    public static SmapiStatus Check(string gamePath, SmapiRelease? latest)
    {
        var exe = GameLocator.SmapiExecutable(gamePath);
        if (exe is null) return new(false, null, latest?.Version.TrimStart('v'), null);
        var ver = InstalledVersion(gamePath);
        string? problem = null;
        if (!Directory.Exists(Path.Combine(gamePath, "smapi-internal"))) problem = Loc.T("Falta la carpeta «smapi-internal»: la instalación está incompleta.");
        else if (!OperatingSystem.IsWindows() && !File.Exists(Path.Combine(gamePath, "StardewModdingAPI"))) problem = Loc.T("Falta el ejecutable de SMAPI para Linux.");
        return new(true, ver, latest?.Version.TrimStart('v'), problem);
    }
}
