using SdvMods.Core.Localization;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.App.Services;
using SdvMods.Core.Profiles;
using SdvMods.Core.Updates;

namespace SdvMods.App.ViewModels;

// App version / self-update notice and profile sharing.
public partial class MainViewModel
{
    // ---------------------------------------------------------------- app version

    /// <summary>Set by the window: file dialogs and clipboard.</summary>
    public Func<string, Task<string?>>? PickSaveFile { get; set; }
    public Func<Task<string?>>? PickProfileFile { get; set; }
    public Func<string, Task>? SetClipboardText { get; set; }
    public Func<Task<string?>>? GetClipboardText { get; set; }

    public string AppVersionText => "v" + AppInfo.Version;
    AppRelease? _newRelease;

    [ObservableProperty] public partial bool NewVersionAvailable { get; set; }
    [ObservableProperty] public partial string NewVersionText { get; set; } = "";
    [ObservableProperty] public partial string AppUpdateInfo { get; set; } = "";

    [RelayCommand] Task CheckAppUpdateAsync() => CheckAppUpdateCoreAsync(announce: true);

    async Task CheckAppUpdateCoreAsync(bool announce)
    {
        var rel = await AppInfo.LatestReleaseAsync(_http);
        if (rel is not null && AppInfo.IsNewer(rel))
        {
            _newRelease = rel;
            NewVersionAvailable = true;
            NewVersionText = Loc.F("Hay una versión nueva de Junimo Manager: {0} (tienes la {1}).", rel.Version, AppInfo.Version);
            AppUpdateInfo = NewVersionText;
        }
        else
        {
            NewVersionAvailable = false;
            AppUpdateInfo = rel is null
                ? Loc.F("Versión {0}. No se pudo comprobar si hay otra más nueva (o aún no hay versiones publicadas).", AppInfo.Version)
                : Loc.F("Versión {0} ✔ — es la última publicada.", AppInfo.Version);
        }
        if (announce) Status = AppUpdateInfo;
    }

    [RelayCommand] void DownloadNewVersion() => Shell.OpenUrl(_newRelease?.AssetUrl ?? _newRelease?.PageUrl ?? AppInfo.ReleasesUrl);
    [RelayCommand] void OpenReleaseNotes() => Shell.OpenUrl(_newRelease?.PageUrl ?? AppInfo.ReleasesUrl);
    [RelayCommand] void OpenRepo() => Shell.OpenUrl(AppInfo.RepoUrl);

    // ---------------------------------------------------------------- share profiles

    [ObservableProperty] public partial string ImportCode { get; set; } = "";

    static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "perfil" : s;
    }

    [RelayCommand]
    async Task ExportProfileFileAsync()
    {
        if (SelectedProfile is not { } sel || PickSaveFile is null) return;
        var path = await PickSaveFile(SafeFileName(sel.Name) + ".junimo");
        if (path is null) return;
        try
        {
            System.Collections.Generic.Dictionary<string, string>? configs = null;
            if (ExportIncludeConfigs)
            {
                if (sel.Profile.Id == _settings.ActiveProfileId) _configStore.Capture(sel.Profile, _installed); // include the latest settings
                configs = _configStore.ReadAll(sel.Profile);
            }
            await File.WriteAllTextAsync(path, ProfileShare.ToJson(sel.Profile, configs));
            Status = Loc.F("Perfil «{0}» guardado en {1}. Pásalo a tus amigos: lo abren con «Importar archivo».", sel.Name, path);
        }
        catch (Exception ex) { Status = Loc.T("No se pudo guardar el archivo: ") + ex.Message; }
    }

    [RelayCommand]
    async Task CopyProfileCodeAsync()
    {
        if (SelectedProfile is not { } sel || SetClipboardText is null) return;
        try
        {
            await SetClipboardText(ProfileShare.ToCode(sel.Profile));
            Status = Loc.F("Código del perfil «{0}» copiado. Pégalo en un mensaje; tus amigos lo usan en «Importar código».", sel.Name);
        }
        catch (Exception ex) { Status = Loc.T("No se pudo copiar al portapapeles: ") + ex.Message; }
    }

    [RelayCommand]
    async Task ImportProfileFileAsync()
    {
        if (PickProfileFile is null) return;
        var path = await PickProfileFile();
        if (path is null) return;
        try { ImportProfileText(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Status = ex.Message; }
    }

    [RelayCommand]
    async Task ImportProfileCodeAsync()
    {
        var text = ImportCode;
        if (string.IsNullOrWhiteSpace(text) && GetClipboardText is not null) text = await GetClipboardText() ?? "";
        if (string.IsNullOrWhiteSpace(text)) { Status = Loc.T("Pega primero el código del perfil (o cópialo y pulsa el botón)."); return; }
        try { ImportProfileText(text); ImportCode = ""; }
        catch (InvalidDataException ex) { Status = ex.Message; }
    }

    void ImportProfileText(string text)
    {
        var (p, configs) = ProfileShare.ImportWithConfigs(text);
        var baseName = p.Name;
        for (var n = 2; _profiles.Active.Any(x => string.Equals(x.Name, p.Name, StringComparison.CurrentCultureIgnoreCase)); n++)
            p.Name = $"{baseName} ({n})";
        _profiles.Add(p);
        _configStore.WriteAll(p.Id, configs);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Profile.Id == p.Id);
        SelectedTab = TabProfiles;
        Status = Loc.F("Perfil «{0}» importado con {1} mods. Pulsa «Activar este perfil» para instalarlo.", p.Name, p.Mods.Count)
                 + (configs.Count > 0 ? Loc.F(" Incluye la configuración de {0} mods.", configs.Count) : "");
        _ = SyncIfConfiguredAsync();
    }
}
