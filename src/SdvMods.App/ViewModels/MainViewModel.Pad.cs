using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.App.Services;
using SdvMods.Core.Localization;

namespace SdvMods.App.ViewModels;

// Language, controller mode and self-update commands.
public partial class MainViewModel
{
    // ---------------------------------------------------------------- language

    /// <summary>"auto" first, then every available language.</summary>
    public IReadOnlyList<string> LanguageNames { get; } =
        new[] { Loc.T("Automático (idioma del sistema)") }.Concat(Loc.Available().Select(a => a.Name)).ToList();
    readonly List<string> _languageCodes = new[] { "auto" }.Concat(Loc.Available().Select(a => a.Code)).ToList();

    [RelayCommand]
    void OpenLanguagesFolder()
    {
        System.IO.Directory.CreateDirectory(Loc.UserDir);
        Shell.OpenUrl(Loc.UserDir);
    }

    /// <summary>Drops a ready-to-translate file (all texts with their English version) in the languages folder and opens it.</summary>
    [RelayCommand]
    void CreateTranslationTemplate()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Loc.UserDir);
            var path = System.IO.Path.Combine(Loc.UserDir, "my-language.json");
            for (var n = 2; System.IO.File.Exists(path); n++) path = System.IO.Path.Combine(Loc.UserDir, $"my-language-{n}.json");
            Loc.WriteTemplate(path);
            Shell.OpenUrl(Loc.UserDir);
            Status = Loc.F("Plantilla creada: {0}. Tradúcela, renómbrala con el código de tu idioma (p. ej. fr.json), reinicia y elígela en Ajustes.", System.IO.Path.GetFileName(path));
        }
        catch (Exception ex) { Status = Loc.F("No se pudo crear la plantilla: {0}", ex.Message); }
    }

    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial bool RestartNeeded { get; set; }

    // ---------------------------------------------------------------- controller mode

    public IReadOnlyList<string> ControllerModeNames { get; } =
        new[] { Loc.T("Automático (solo al abrir desde Steam)"), Loc.T("Siempre"), Loc.T("Nunca") };

    [ObservableProperty] public partial int ControllerModeIndex { get; set; }
    /// <summary>What the controller layer sees right now (for diagnosing a pad that does nothing).</summary>
    [ObservableProperty] public partial string PadStatus { get; set; } = "";
    public string PadLogPath => SdvMods.App.Input.GamepadLog.Path_;
    [ObservableProperty] public partial bool ControllerConnected { get; set; }

    /// <summary>Big buttons and the controller hint bar.</summary>
    public bool IsPadMode => ControllerModeIndex switch
    {
        1 => true,
        2 => false,
        _ => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId")),
    };

    public bool ShowPadHints => IsPadMode || ControllerConnected;
    partial void OnControllerConnectedChanged(bool value) => OnPropertyChanged(nameof(ShowPadHints));

    void LoadLanguageAndMode()
    {
        var i = _languageCodes.IndexOf(_settings.Language);
        LanguageIndex = i < 0 ? 0 : i;
        ControllerModeIndex = _settings.ControllerMode switch { "on" => 1, "off" => 2, _ => 0 };
    }

    void SaveLanguageAndMode()
    {
        var code = _languageCodes[Math.Clamp(LanguageIndex, 0, _languageCodes.Count - 1)];
        if (code != _settings.Language) RestartNeeded = true;
        _settings.Language = code;
        var mode = ControllerModeIndex switch { 1 => "on", 2 => "off", _ => "auto" };
        if (mode != _settings.ControllerMode) RestartNeeded = true;
        _settings.ControllerMode = mode;
    }

    [RelayCommand]
    void RestartApp()
    {
        try
        {
            AppUpdater.Relaunch();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex) { Status = Loc.F("No se pudo reiniciar: {0}", ex.Message); }
    }

    // ---------------------------------------------------------------- update this app in place

    [RelayCommand]
    async System.Threading.Tasks.Task UpdateAppNowAsync()
    {
        if (!SdvMods.Core.Edition.SelfUpdate || _newRelease is not { } rel) return;
        if (!AppUpdater.CanSelfUpdate || rel.AssetUrl is null) { DownloadNewVersion(); return; }
        await RunAsync(Loc.F("Descargando Junimo Manager {0}…", rel.Version), async () =>
        {
            var progress = new Progress<double>(p => Avalonia.Threading.Dispatcher.UIThread.Post(() => { ProgressIndeterminate = p < 0; Progress = Math.Max(p, 0) * 100; }));
            var dir = await AppUpdater.DownloadAsync(_http, rel, progress);
            Status = Loc.T("Instalando la actualización y reiniciando…");
            AppUpdater.ApplyAndRestart(dir);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        });
    }
}
