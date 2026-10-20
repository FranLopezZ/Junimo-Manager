using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SdvMods.App.Services;
using SdvMods.App.ViewModels;
using SdvMods.App.Views;

namespace SdvMods.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Localizer.Install();
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            window.Opened += async (_, _) =>
            {
                await vm.InitializeAsync();
                vm.HandleExternalArgs(Program.StartupArgs);
                SingleInstance.Attach(args => Dispatcher.UIThread.Post(() =>
                {
                    vm.HandleExternalArgs(args);
                    window.Activate();
                }));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
