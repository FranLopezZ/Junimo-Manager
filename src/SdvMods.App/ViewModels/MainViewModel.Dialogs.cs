using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.App.Services;
using SdvMods.Core.Localization;
using SdvMods.Core.Mods;

namespace SdvMods.App.ViewModels;

// Modal overlays: the generic question dialog (dependants / requirements) and the config.json editor.
public partial class MainViewModel
{
    // ---------------------------------------------------------------- which overlay is on top (the controller keeps the focus inside it)

    public bool ModalOpen => DialogVisible || ConfigVisible || DetailVisible;
    public string? ModalOverlayName => DialogVisible ? "DialogOverlay" : ConfigVisible ? "ConfigOverlay" : DetailVisible ? "DetailOverlay" : null;

    void NotifyModal() { OnPropertyChanged(nameof(ModalOpen)); OnPropertyChanged(nameof(ModalOverlayName)); }
    partial void OnDetailVisibleChanged(bool value) => NotifyModal();
    partial void OnDialogVisibleChanged(bool value) => NotifyModal();
    partial void OnConfigVisibleChanged(bool value) => NotifyModal();

    /// <summary>"Back" on the controller / Escape: closes the overlay that is on top.</summary>
    public void CloseTopModal()
    {
        if (DialogVisible) DialogCancel();
        else if (ConfigVisible) CloseConfig();
        else if (DetailVisible) CloseDetail();
    }

    // ---------------------------------------------------------------- question dialog

    TaskCompletionSource<int>? _dialog;
    public ObservableCollection<string> DialogItems { get; } = new();

    [ObservableProperty] public partial bool DialogVisible { get; set; }
    [ObservableProperty] public partial string DialogTitle { get; set; } = "";
    [ObservableProperty] public partial string DialogMessage { get; set; } = "";
    [ObservableProperty] public partial string DialogPrimaryText { get; set; } = "";
    [ObservableProperty] public partial string DialogSecondaryText { get; set; } = "";

    /// <summary>Shows the dialog and waits: 0 = cancelled, 1 = primary button, 2 = secondary button.</summary>
    Task<int> AskAsync(string title, string message, System.Collections.Generic.IEnumerable<string> items, string primary, string secondary)
    {
        _dialog?.TrySetResult(0);
        DialogTitle = title; DialogMessage = message; DialogPrimaryText = primary; DialogSecondaryText = secondary;
        DialogItems.Clear();
        foreach (var i in items) DialogItems.Add(i);
        _dialog = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogVisible = true;
        return _dialog.Task;
    }

    void CloseDialog(int result)
    {
        DialogVisible = false;
        _dialog?.TrySetResult(result);
        _dialog = null;
    }

    [RelayCommand] void DialogPrimary() => CloseDialog(1);
    [RelayCommand] void DialogSecondary() => CloseDialog(2);
    [RelayCommand] void DialogCancel() => CloseDialog(0);

    // ---------------------------------------------------------------- mod config.json editor

    ModConfigDocument? _config;
    ModItem? _configMod;
    public ObservableCollection<ConfigFieldItem> ConfigFields { get; } = new();

    [ObservableProperty] public partial bool ConfigVisible { get; set; }
    [ObservableProperty] public partial string ConfigTitle { get; set; } = "";
    [ObservableProperty] public partial string ConfigPath { get; set; } = "";
    [ObservableProperty] public partial string ConfigMessage { get; set; } = "";
    public bool HasConfigMessage => ConfigMessage.Length > 0;
    partial void OnConfigMessageChanged(string value) => OnPropertyChanged(nameof(HasConfigMessage));
    [ObservableProperty] public partial bool ConfirmRestoreDefaults { get; set; }
    public string RestoreDefaultsText => ConfirmRestoreDefaults ? Loc.T("¿Seguro? Se borra el archivo") : Loc.T("Restaurar valores por defecto");
    partial void OnConfirmRestoreDefaultsChanged(bool value) => OnPropertyChanged(nameof(RestoreDefaultsText));

    Task ShowConfigAsync(ModItem item)
    {
        try
        {
            _config = ModConfigDocument.Load(ModConfigDocument.ConfigPath(item.Mod));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("«{0}»: {1}", item.Name, ex.Message);
            return Task.CompletedTask;
        }
        _configMod = item;
        ConfigFields.Clear();
        foreach (var f in _config.Fields) ConfigFields.Add(new ConfigFieldItem(f));
        ConfigTitle = Loc.F("Configuración de «{0}»", item.Name);
        ConfigPath = _config.FilePath;
        ConfigMessage = _config.Fields.Count == 0 ? Loc.T("Este config.json está vacío.") : "";
        ConfirmRestoreDefaults = false;
        DetailVisible = false;
        ConfigVisible = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    void SaveConfig()
    {
        if (_config is null || _configMod is null) return;
        try
        {
            var ok = _config.TrySave();
            foreach (var f in ConfigFields) f.RefreshError();
            if (!ok) { ConfigMessage = Loc.T("Hay valores que no son válidos; corrígelos y vuelve a guardar."); return; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfigMessage = Loc.F("No se pudo guardar: {0}", ex.Message);
            return;
        }
        Status = Loc.F("Configuración de «{0}» guardada. Se aplica la próxima vez que inicies el juego (copia anterior: config.json.bak).", _configMod.Name);
        CloseConfig();
        CaptureActiveConfigs();
    }

    [RelayCommand]
    void CloseConfig()
    {
        ConfigVisible = false;
        ConfigFields.Clear();
        _config = null; _configMod = null;
    }

    [RelayCommand]
    async Task RestoreConfigDefaultsAsync()
    {
        if (_config is null || _configMod is null) return;
        if (!ConfirmRestoreDefaults) { ConfirmRestoreDefaults = true; return; }
        var name = _configMod.Name;
        try { ModConfigDocument.RestoreDefaults(_config.FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ConfigMessage = Loc.F("No se pudo restaurar: {0}", ex.Message); return; }
        CloseConfig();
        await RefreshModsAsync();
        CaptureActiveConfigs();
        Status = Loc.F("Se borró el config.json de «{0}» (copia en config.json.bak): el mod creará uno nuevo con sus valores por defecto la próxima vez que inicies el juego.", name);
    }

    [RelayCommand]
    void OpenConfigFolder()
    {
        if (_configMod is not null) Shell.OpenUrl(_configMod.Mod.Folder);
    }

    // The mod sheet can open the editor for the mod it shows.
    ModItem? _detailMod;
    public bool DetailHasConfig => _detailMod?.HasConfig == true;
    [RelayCommand] Task OpenDetailConfigAsync() => _detailMod is { HasConfig: true } m ? ShowConfigAsync(m) : Task.CompletedTask;

    /// <summary>JUNIMO_DEBUG_SHOW="config:Mod name" or "dialog:Mod name" opens that overlay at startup, for taking screenshots.</summary>
    async Task DebugShowAsync()
    {
        if (Environment.GetEnvironmentVariable("JUNIMO_DEBUG_SHOW") is not { Length: > 0 } what) return;
        var parts = what.Split(':', 2);
        var item = _allMods.FirstOrDefault(m => m.Name == (parts.Length > 1 ? parts[1] : ""));
        if (item is null) return;
        if (parts[0] == "config") await ShowConfigAsync(item);
        else if (parts[0] == "dialog") _ = ToggleModAsync(item);
        else if (parts[0] == "detail") await ShowModDetailAsync(item);
    }
}
