using System.Text.RegularExpressions;

namespace SdvMods.Core.Game;

/// <summary>Finds the Stardew Valley install folder (Steam on Windows, Linux and Steam Deck, incl. SD card).</summary>
public static partial class GameLocator
{
    const string FolderName = "Stardew Valley";
    const string SteamAppId = "413150";

    public static bool IsGameFolder(string? path)
        => !string.IsNullOrEmpty(path) && Directory.Exists(path) &&
           (File.Exists(Path.Combine(path, "Stardew Valley.dll")) || File.Exists(Path.Combine(path, "Stardew Valley.exe")));

    public static string? Find()
        => Candidates().Where(Directory.Exists).FirstOrDefault(IsGameFolder);

    public static IEnumerable<string> Candidates()
    {
        foreach (var steam in SteamRoots())
        {
            foreach (var lib in LibraryFolders(steam))
                yield return Path.Combine(lib, "steamapps", "common", FolderName);
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
                if (pf is not null) yield return Path.Combine(pf, "Steam", "steamapps", "common", FolderName);
            foreach (var pf in new[] { @"C:\GOG Games\Stardew Valley", @"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley" })
                yield return pf;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "GOG Games", FolderName, "game");
        }
    }

    static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            foreach (var sub in new[] { @"Software\Valve\Steam", @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                string? v = null;
                try
                {
                    using var k = hive.OpenSubKey(sub);
                    v = (k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(v)) yield return v.Replace('/', '\\');
            }
#pragma warning restore CA1416
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".steam", "root");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        }
    }

    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"")]
    private static partial Regex PathLine();

    public static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        yield return steamRoot;
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }
        foreach (Match m in PathLine().Matches(text))
            yield return m.Groups["p"].Value.Replace("\\\\", "\\");
    }

    /// <summary>Where mods live: the game's Mods folder.</summary>
    public static string ModsFolder(string gamePath) => Path.Combine(gamePath, "Mods");

    public static string? SmapiExecutable(string gamePath)
    {
        foreach (var n in new[] { "StardewModdingAPI.exe", "StardewModdingAPI", "StardewModdingAPI.dll" })
        {
            var p = Path.Combine(gamePath, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool SmapiInstalled(string gamePath) => SmapiExecutable(gamePath) is not null;
}
