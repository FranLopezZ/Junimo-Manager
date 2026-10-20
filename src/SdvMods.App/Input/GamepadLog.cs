using System;
using System.IO;
using SdvMods.Core.Settings;

namespace SdvMods.App.Input;

/// <summary>Tiny diagnostic log (config folder, gamepad.log) so "the controller does nothing" can be diagnosed on a real device.</summary>
public static class GamepadLog
{
    static string FilePath => Path.Combine(AppPaths.ConfigDir, "gamepad.log");
    static int _keyEvents;

    public static string Path_ => FilePath;

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 100_000) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break the app */ }
    }

    /// <summary>The first few keyboard events: shows whether Steam Input is turning the controller into keys.</summary>
    public static void KeyEvent(string key)
    {
        if (_keyEvents++ < 25) Write("key event: " + key);
    }

    public static void Startup(bool padMode)
    {
        Write($"start v{SdvMods.Core.Updates.AppInfo.Version} padMode={padMode} SteamGameId={Environment.GetEnvironmentVariable("SteamGameId")} " +
              $"SteamDeck={Environment.GetEnvironmentVariable("SteamDeck")} desktop={Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")} " +
              $"session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} display={Environment.GetEnvironmentVariable("DISPLAY")} wayland={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}");
    }
}
