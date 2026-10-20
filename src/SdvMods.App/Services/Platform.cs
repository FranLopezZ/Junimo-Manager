using SdvMods.Core.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace SdvMods.App.Services;

/// <summary>Makes sure only one manager runs, so an nxm:// click in the browser reaches the window that is already open.</summary>
public static class SingleInstance
{
    static string PipeName => "JunimoManager." + Environment.UserName;
    static readonly List<string[]> Pending = new();
    static Action<string[]>? _handler;
    static readonly object Gate = new();

    public static bool TrySendToRunning(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.Write(string.Join('\n', args));
            return true;
        }
        catch { return false; }
    }

    public static void StartServer()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var text = await r.ReadToEndAsync();
                    Dispatch(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch { await Task.Delay(500); }
            }
        });
    }

    static void Dispatch(string[] args)
    {
        lock (Gate)
        {
            if (_handler is null) Pending.Add(args); else _handler(args);
        }
    }

    /// <summary>Registers the UI handler and delivers anything that arrived while the window was starting.</summary>
    public static void Attach(Action<string[]> handler)
    {
        lock (Gate)
        {
            _handler = handler;
            foreach (var a in Pending) handler(a);
            Pending.Clear();
        }
    }
}

/// <summary>Registers this app as the handler of nxm:// links ("Mod Manager Download" on Nexus Mods).</summary>
public static class NxmRegistration
{
    public static string? ExecutablePath
        => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } ai ? ai : Environment.ProcessPath;

    public static bool CanRegister
        => ExecutablePath is { } p && !Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    static string DesktopFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications", "junimomanager.desktop");

    /// <summary>
    /// A new version usually lives in a new folder, which silently breaks the nxm:// handler. If this app was registered
    /// before (the handler points at some JunimoManager executable), point it at the running copy again.
    /// </summary>
    public static void RefreshIfRegistered()
    {
        try
        {
            if (!CanRegister || ExecutablePath is not { } exe) return;
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\nxm\shell\open\command");
                var current = k?.GetValue("") as string;
#pragma warning restore CA1416
                if (current is null || !current.Contains("JunimoManager", StringComparison.OrdinalIgnoreCase)) return;
                if (current.Contains($"\"{exe}\"", StringComparison.OrdinalIgnoreCase)) return;
            }
            else
            {
                if (!File.Exists(DesktopFile)) return;
                var text = File.ReadAllText(DesktopFile);
                if (!text.Contains("JunimoManager", StringComparison.OrdinalIgnoreCase) || text.Contains($"Exec=\"{exe}\"")) return;
            }
            Register();
        }
        catch { /* best effort */ }
    }

    public static void Register()
    {
        var exe = ExecutablePath ?? throw new InvalidOperationException(Loc.T("No se pudo determinar la ruta del ejecutable."));
        if (!CanRegister) throw new InvalidOperationException(Loc.T("Solo se puede registrar desde la versión publicada (no desde 'dotnet run')."));

        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm");
            root.SetValue("", "URL:NXM Protocol");
            root.SetValue("URL Protocol", "");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
#pragma warning restore CA1416
        }
        else
        {
            var apps = Path.GetDirectoryName(DesktopFile)!;
            Directory.CreateDirectory(apps);
            var icon = Path.Combine(Path.GetDirectoryName(exe)!, "JunimoManager.png");
            File.WriteAllText(DesktopFile,
                "[Desktop Entry]\nType=Application\nName=Junimo Manager\nComment=Gestor de mods de Stardew Valley\n" +
                $"Exec=\"{exe}\" %u\n" + (File.Exists(icon) ? $"Icon={icon}\n" : "") + "MimeType=x-scheme-handler/nxm;\nTerminal=false\nCategories=Game;\n");
            Run("xdg-mime", "default junimomanager.desktop x-scheme-handler/nxm");
            Run("update-desktop-database", $"\"{apps}\"");
        }
    }

    static void Run(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000); } catch { }
    }
}

public static class Shell
{
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url) { UseShellExecute = false });
        }
        catch { }
    }
}
