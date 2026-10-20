using SdvMods.Core.Localization;
using Avalonia.Input;
using Avalonia.Input.Platform;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SdvMods.App.ViewModels;

namespace SdvMods.App.Views;

public partial class MainWindow : Window
{
    SdvMods.App.Input.GamepadNavigator? _pad;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PickFolder = PickFolderAsync;
        vm.PickArchive = PickArchiveAsync;
        Activated += (_, _) => vm.CaptureActiveConfigs();
        Closing += (_, _) => vm.CaptureActiveConfigs();
        if (_pad is null)
        {
            if (vm.IsPadMode) { Classes.Add("pad"); WindowState = WindowState.Maximized; }
            _pad = new SdvMods.App.Input.GamepadNavigator(this, vm);
            _pad.Start();
        }
        vm.PickSaveFile = SaveFileAsync;
        vm.PickProfileFile = PickProfileFileAsync;
        vm.SetClipboardText = text => Clipboard is { } c ? c.SetTextAsync(text) : Task.CompletedTask;
        vm.GetClipboardText = async () => Clipboard is { } c ? await c.TryGetTextAsync() : null;
    }

    /// <summary>
    /// In controller mode the arrows (from Steam Input or a keyboard) must move between controls even when a drop-down has the focus,
    /// instead of changing its selection by accident.
    /// </summary>
    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pad is null || DataContext is not MainViewModel { IsPadMode: true }) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
        if (e.Source is ComboBox { IsDropDownOpen: false } or Avalonia.Controls.Primitives.ToggleButton)
        {
            if (_pad.HandleKey(e.Key)) e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyModifiers == KeyModifiers.None && _pad?.HandleKey(e.Key) == true) e.Handled = true;
    }

    void OnShowUpdates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedTab = MainViewModel.TabUpdates;
    }

    async Task<string?> PickFolderAsync()
    {
        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    async Task<string?> SaveFileAsync(string suggestedName)
    {
        var r = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Guardar perfil para compartir"),
            SuggestedFileName = suggestedName,
            DefaultExtension = "junimo",
            FileTypeChoices = new[] { new FilePickerFileType(Loc.T("Perfil de Junimo Manager")) { Patterns = new[] { "*.junimo" } } },
        });
        return r?.TryGetLocalPath();
    }

    async Task<string?> PickProfileFileAsync()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Loc.T("Elige un perfil compartido (.junimo)"),
            FileTypeFilter = new[] { new FilePickerFileType(Loc.T("Perfil de Junimo Manager")) { Patterns = new[] { "*.junimo", "*.json" } } },
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    async Task<string?> PickArchiveAsync()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = Loc.T("Elige el archivo del mod (.zip, .7z, .rar)"),
            FileTypeFilter = new[] { new FilePickerFileType("Mods") { Patterns = new[] { "*.zip", "*.7z", "*.rar" } } },
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }
}
