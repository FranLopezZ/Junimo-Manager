using Avalonia;
using System;
using SdvMods.App.Services;
using SdvMods.Core.Localization;

namespace SdvMods.App;

sealed class Program
{
    /// <summary>nxm:// links given on the command line (browser click) or forwarded by a second instance.</summary>
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--version") { Console.WriteLine(SdvMods.Core.Updates.AppInfo.Version); return 0; }
        AppUpdater.HandleStartup(ref args);
        Loc.Init(Environment.GetEnvironmentVariable("JUNIMO_LANG") is { Length: > 0 } forced ? forced : SdvMods.Core.Settings.AppSettings.Load().Language);
        if (SingleInstance.TrySendToRunning(args)) return 0;
        SingleInstance.StartServer();
        StartupArgs = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
