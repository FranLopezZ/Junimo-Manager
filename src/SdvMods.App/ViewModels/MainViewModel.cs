using SdvMods.Core.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.App.Services;
using SdvMods.Core.Game;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;
using SdvMods.Core.Settings;
using SdvMods.Core.Sync;
using SdvMods.Core.Updates;

namespace SdvMods.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly HttpClient _http = Http.Create();
    readonly GitHubSession _gh;
    readonly ProfileService _profiles = new(AppPaths.ProfilesFile);
    readonly HashSet<string> _pendingInstallIds = new(StringComparer.OrdinalIgnoreCase);
    List<InstalledMod> _installed = new();
    readonly ProfileConfigStore _configStore = new(AppPaths.ProfileConfigsDir);
    readonly ImageCache _images;
    readonly NexusCatalog _catalog;

    public const int TabMods = 0, TabExplore = 1, TabProfiles = 2, TabUpdates = 3, TabSettings = 4;

    /// <summary>Set by the window: opens the native pickers.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }
    public Func<Task<string?>>? PickArchive { get; set; }

    public ObservableCollection<ModItem> Mods { get; } = new();
    public ObservableCollection<UpdateItem> Updates { get; } = new();
    public ObservableCollection<ProfileItem> Profiles { get; } = new();
    public ObservableCollection<string> SelectedProfileMods { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    /// <summary>Mods of the active profile that could not be installed automatically (and why).</summary>
    public ObservableCollection<string> MissingFromProfile { get; } = new();
    public bool HasMissing => MissingFromProfile.Count > 0;
    public int PendingNewCount => Updates.Count(u => u.IsNew);
    public bool HasPendingNew => PendingNewCount > 0;
    public string PendingNewText => Loc.F("Faltan {0} mods del perfil por instalar (necesitan un clic en Nexus).", PendingNewCount);

    [ObservableProperty] public partial string GamePath { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = Loc.T("Listo.");
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressIndeterminate { get; set; }
    [ObservableProperty] public partial ProfileItem? SelectedProfile { get; set; }
    [ObservableProperty] public partial string NewProfileName { get; set; } = "";
    [ObservableProperty] public partial string RenameText { get; set; } = "";
    [ObservableProperty] public partial string NexusApiKey { get; set; } = "";
    [ObservableProperty] public partial string GitHubToken { get; set; } = "";
    [ObservableProperty] public partial string SyncFolder { get; set; } = "";
    [ObservableProperty] public partial int SyncModeIndex { get; set; }
    [ObservableProperty] public partial bool SyncOnStartup { get; set; } = true;
    [ObservableProperty] public partial bool PerProfileConfigs { get; set; } = true;
    [ObservableProperty] public partial bool ExportIncludeConfigs { get; set; } = true;
    [ObservableProperty] public partial bool GameFound { get; set; }
    [ObservableProperty] public partial bool SmapiMissing { get; set; }
    [ObservableProperty] public partial string ActiveProfileText { get; set; } = Loc.T("Sin perfil activo");
    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool CanPlay { get; set; }
    [ObservableProperty] public partial bool LoginInProgress { get; set; }
    [ObservableProperty] public partial string LoginCode { get; set; } = "";
    [ObservableProperty] public partial bool GitHubLoggedIn { get; set; }
    [ObservableProperty] public partial string LoginUrl { get; set; } = "";
    [ObservableProperty] public partial Bitmap? LoginQr { get; set; }

    CancellationTokenSource? _loginCts;
    public bool GitHubLoginAvailable => Branding.EffectiveGitHubClientId.Length > 0;

    public bool HasUpdates => Updates.Count > 0;
    public string UpdatesTabHeader => Updates.Count > 0 ? Loc.F("Actualizaciones ({0})", Updates.Count) : Loc.T("Actualizaciones");
    public bool HasWarnings => Warnings.Count > 0;
    public bool SyncUsesGist => SyncModeIndex == 1;
    public bool SyncUsesFolder => SyncModeIndex == 2;
    public bool CanRegisterNxm => NxmRegistration.CanRegister;
    public string Platform => OperatingSystem.IsWindows() ? "Windows" : "Linux / Steam Deck";

    public MainViewModel()
    {
        _gh = new GitHubSession(_http, Branding.EffectiveGitHubClientId, _settings.GitHubToken, _settings.GitHubRefreshToken, _settings.GitHubTokenExpiresAt);
        _gh.Changed += SaveGitHubSession;
        _images = new ImageCache(_http);
        _catalog = new NexusCatalog(_http);
        Updates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUpdates)); OnPropertyChanged(nameof(UpdatesTabHeader));
            OnPropertyChanged(nameof(PendingNewCount)); OnPropertyChanged(nameof(HasPendingNew)); OnPropertyChanged(nameof(PendingNewText));
        };
        MissingFromProfile.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMissing));
        Warnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWarnings));
        GamePath = _settings.GamePath ?? "";
        NexusApiKey = _settings.NexusApiKey ?? "";
        GitHubToken = _settings.GitHubToken ?? "";
        GitHubLoggedIn = GitHubToken.Length > 0;
        SyncFolder = _settings.SyncFolder ?? "";
        SyncModeIndex = (int)_settings.SyncMode;
        SyncOnStartup = _settings.SyncOnStartup;
        PerProfileConfigs = _settings.PerProfileConfigs;
        SyncAuto = _settings.SyncAuto;
        LoadLanguageAndMode();
    }

    // ---------------------------------------------------------------- startup

    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(GamePath) || !GameLocator.IsGameFolder(GamePath))
        {
            var found = GameLocator.Find();
            if (found is not null) { GamePath = found; _settings.GamePath = found; _settings.Save(); }
        }
        NxmRegistration.RefreshIfRegistered();
        if (int.TryParse(Environment.GetEnvironmentVariable("JUNIMO_START_TAB"), out var startTab)) SelectedTab = startTab; // debugging aid
        ReloadProfiles();
        await RefreshModsAsync();
        CaptureActiveConfigs();
        _ = RefreshSmapiAsync(queryLatest: true);
        _ = CheckAppUpdateCoreAsync(announce: false);
        RefreshSyncStatus();
        if (SyncOnStartup && SyncConfigured) await SyncAsync(silent: true);
        await DebugShowAsync(); // debugging aid for screenshots (JUNIMO_DEBUG_SHOW)
    }

    partial void OnGamePathChanged(string value) => UpdateGameState();

    void UpdateGameState()
    {
        GameFound = GameLocator.IsGameFolder(GamePath);
        SmapiMissing = GameFound && !GameLocator.SmapiInstalled(GamePath);
        CanPlay = GameFound && !SmapiMissing;
        UpdateSmapiTexts();
    }

    partial void OnSyncModeIndexChanged(int value)
    {
        RefreshSyncStatus();
        OnPropertyChanged(nameof(SyncUsesGist));
        OnPropertyChanged(nameof(SyncUsesFolder));
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        SelectedProfileMods.Clear();
        RenameText = value?.Name ?? "";
        NotifySelectedProfileSync();
        if (value is null) return;
        foreach (var m in value.Profile.Mods)
            SelectedProfileMods.Add($"{(m.Enabled ? "●" : "○")}  {m.Name}  {m.Version}");
    }

    string ModsFolder => GameLocator.ModsFolder(GamePath);
    ModInstaller Installer => new(ModsFolder, AppPaths.BackupsDir(GamePath));
    Profile? ActiveProfile => _profiles.Find(_settings.ActiveProfileId);
    bool SyncConfigured => (SyncMode)SyncModeIndex switch
    {
        SyncMode.Gist => !string.IsNullOrWhiteSpace(GitHubToken),
        SyncMode.Folder => !string.IsNullOrWhiteSpace(SyncFolder),
        _ => false,
    };

    // ---------------------------------------------------------------- busy helper

    async Task RunAsync(string working, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true; ProgressIndeterminate = true; Progress = 0; Status = working;
        try { await _captureInFlight; await work(); }
        catch (Exception ex) { Status = Loc.F("Error: {0}", ex.Message); }
        finally { IsBusy = false; ProgressIndeterminate = false; }
    }

    // ---------------------------------------------------------------- mods

    [RelayCommand]
    public async Task RefreshModsAsync()
    {
        if (!GameFound) { _allMods.Clear(); Mods.Clear(); Status = Loc.T("Configura la carpeta del juego en Ajustes."); return; }
        var path = ModsFolder;
        _installed = await Task.Run(() => ModScanner.Scan(path));
        var keepSelected = _allMods.Where(m => m.IsSelected).Select(m => m.Mod.UniqueID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in _allMods) old.PropertyChanged -= OnModItemChanged;
        _allMods.Clear();
        var profile = ActiveProfile;
        foreach (var m in _installed.Where(m => !ModScanner.IsBundled(m)).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new ModItem(m, profile is not null, profile is not null && ProfileService.IsMember(profile, m.UniqueID),
                ToggleModAsync, UninstallModAsync, ShowModDetailAsync, ToggleMembershipAsync, ShowConfigAsync) { IsSelected = keepSelected.Contains(m.UniqueID) };
            item.PropertyChanged += OnModItemChanged;
            _allMods.Add(item);
        }
        OnPropertyChanged(nameof(HasActiveProfile));
        ApplyModFilter();
        _ = LoadModThumbsAsync(_allMods.ToList());

        Warnings.Clear();
        foreach (var d in ModScanner.FindMissingDependencies(_installed))
            Warnings.Add(DependencyWarning(d));
        Status = Loc.F("{0} mods encontrados.", _allMods.Count);
    }

    static string DependencyWarning(ModScanner.MissingDependency d)
        => d.Disabled
            ? Loc.F("«{0}» necesita «{1}» (está desactivado)", d.Mod.Name, d.DependencyId)
            : Loc.F("«{0}» necesita «{1}» (falta o versión antigua)", d.Mod.Name, d.DependencyId);

    Task ToggleModAsync(ModItem item) => BatchAsync(new List<ModItem> { item }, item.Enabled ? BatchOp.Disable : BatchOp.Enable);

    Task UninstallModAsync(ModItem item) => BatchAsync(new List<ModItem> { item }, BatchOp.Uninstall);

    [RelayCommand]
    async Task InstallFromFileAsync()
    {
        if (PickArchive is null || !GameFound) return;
        var file = await PickArchive();
        if (file is null) return;
        await InstallArchiveAsync(file, null);
    }

    async Task InstallArchiveAsync(string file, string? expectedId)
    {
        await RunAsync(Loc.T("Instalando…"), async () =>
        {
            var result = await Task.Run(() => Installer.InstallArchive(file, expectedId));
            await AfterModsChangedAsync(result.Installed.Select(i => i.UniqueID));
            Warnings.Clear();
            foreach (var d in ModScanner.FindMissingDependencies(_installed)) Warnings.Add(DependencyWarning(d));
            Status = Loc.F("Instalado: {0}.", string.Join(", ", result.Installed.Select(i => i.Name))) +
                     (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "");
            foreach (var done in result.Installed) RemoveUpdate(done.UniqueID);
        });
    }

    /// <summary>Refresh, then keep the active profile (and the cloud copy) in step with what is on disk.</summary>
    /// <param name="added">Mods just installed or turned on: they join the active profile (other mods on disk do not).</param>
    async Task AfterModsChangedAsync(IEnumerable<string>? added = null)
    {
        await RefreshModsAsync();
        if (ActiveProfile is { } p)
        {
            foreach (var id in added ?? Enumerable.Empty<string>())
                if (_installed.FirstOrDefault(m => string.Equals(m.UniqueID, id, StringComparison.OrdinalIgnoreCase)) is { } mod)
                    _profiles.UpsertMod(p, mod, mod.Enabled);
            _profiles.RefreshMembers(p, _installed);
            ReloadProfiles();
            await RefreshModsAsync(); // membership may have changed what the list shows
            _ = SyncIfConfiguredAsync();
        }
    }

    // ---------------------------------------------------------------- updates

    [RelayCommand]
    async Task CheckUpdatesAsync()
    {
        if (!GameFound) return;
        await RunAsync(Loc.T("Buscando actualizaciones…"), async () =>
        {
            var svc = new UpdateService(_http, await _gh.GetValidTokenAsync());
            var found = await svc.CheckAsync(_installed);
            foreach (var old in Updates.Where(u => !u.IsNew).ToList()) Updates.Remove(old);
            foreach (var u in found) Updates.Add(new UpdateItem(u, false, ActOnUpdateAsync));
            Status = found.Count == 0 ? Loc.T("Todos los mods están al día.") : $"{found.Count} actualizaciones disponibles.";
            if (found.Count > 0) SelectedTab = TabUpdates;
        });
    }

    [RelayCommand]
    async Task UpdateAllAsync()
    {
        var autos = Updates.Where(u => u.Update.Method == UpdateMethod.Auto).ToList();
        if (autos.Count == 0) { Status = Loc.T("No hay actualizaciones automáticas; las de Nexus requieren un clic en la web."); return; }
        foreach (var u in autos) await ActOnUpdateAsync(u);
        var manual = Updates.Count;
        Status = manual == 0 ? Loc.T("Todo actualizado.") : Loc.F("Quedan {0} que necesitan acción manual (Nexus/otros).", manual);
    }

    async Task ActOnUpdateAsync(UpdateItem item)
    {
        var u = item.Update;
        switch (u.Method)
        {
            case UpdateMethod.Auto:
                await RunAsync(Loc.F("Descargando «{0}»…", u.Name), async () =>
                {
                    await InstallAutoCoreAsync(u);
                    await AfterModsChangedAsync();
                    Status = Loc.F("«{0}» actualizado a {1}.", u.Name, u.NewVersion);
                });
                break;
            case UpdateMethod.NexusBrowser:
                await OpenNexusAsync(u);
                break;
            default:
                if (u.PageUrl is not null) Shell.OpenUrl(u.PageUrl);
                break;
        }
    }

    /// <summary>Download + install a direct (GitHub) update. Does not refresh or touch the profile; callers do.</summary>
    async Task InstallAutoCoreAsync(ModUpdate u)
    {
        var dest = Path.Combine(AppPaths.CacheDir, u.FileName ?? (u.UniqueID + ".zip"));
        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => { ProgressIndeterminate = p < 0; Progress = Math.Max(p, 0) * 100; }));
        await Http.DownloadAsync(_http, u.DownloadUrl!, dest, progress);
        await Task.Run(() => Installer.InstallArchive(dest, u.UniqueID));
        TryDelete(dest);
        RemoveUpdate(u.UniqueID);
        _pendingInstallIds.Remove(u.UniqueID);
    }

    async Task OpenNexusAsync(ModUpdate u)
    {
        if (u.NexusModId is not { } id) { if (u.PageUrl is not null) Shell.OpenUrl(u.PageUrl); return; }
        await OpenNexusPageAsync(id);
    }

    async Task OpenNexusPageAsync(int id)
    {
        int? fileId = null;
        if (!string.IsNullOrWhiteSpace(NexusApiKey))
        {
            try { fileId = (await new NexusClient(_http, NexusApiKey).GetMainFileAsync(id))?.FileId; } catch { }
        }
        Shell.OpenUrl(NexusClient.FilePageUrl(id, fileId));
        Status = string.IsNullOrWhiteSpace(NexusApiKey)
            ? Loc.T("Se abrió Nexus. Añade tu API key en Ajustes para que la descarga se complete sola tras el clic.")
            : Loc.T("Pulsa «Descarga lenta / Mod Manager» en la página de Nexus; el resto es automático.");
    }

    void RemoveUpdate(string uniqueId)
    {
        foreach (var x in Updates.Where(x => string.Equals(x.Update.UniqueID, uniqueId, StringComparison.OrdinalIgnoreCase)).ToList()) Updates.Remove(x);
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    // ---------------------------------------------------------------- nxm:// (Nexus "Mod Manager Download")

    public void HandleExternalArgs(string[] args)
    {
        foreach (var a in args.Where(NxmLink.IsNxm))
        {
            var link = NxmLink.Parse(a);
            if (link is not null) Dispatcher.UIThread.Post(async () => await HandleNxmAsync(link));
        }
    }

    async Task HandleNxmAsync(NxmLink link)
    {
        if (!GameFound) { Status = Loc.T("Configura la carpeta del juego antes de instalar mods."); return; }
        if (string.IsNullOrWhiteSpace(NexusApiKey))
        {
            SelectedTab = TabSettings;
            Status = Loc.T("Para completar descargas de Nexus necesitas tu API key (Ajustes).");
            return;
        }
        var expected = Updates.FirstOrDefault(u => u.Update.NexusModId == link.ModId)?.Update;
        await RunAsync(Loc.T("Descargando desde Nexus…"), async () =>
        {
            var url = await new NexusClient(_http, NexusApiKey).GetDownloadUrlAsync(link);
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            var dest = Path.Combine(AppPaths.CacheDir, string.IsNullOrEmpty(name) ? $"nexus-{link.ModId}-{link.FileId}.zip" : Uri.UnescapeDataString(name));
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => { ProgressIndeterminate = p < 0; Progress = Math.Max(p, 0) * 100; }));
            await Http.DownloadAsync(_http, url, dest, progress);
            var result = await Task.Run(() => Installer.InstallArchive(dest, expected?.UniqueID));
            TryDelete(dest);
            foreach (var i in result.Installed) { RemoveUpdate(i.UniqueID); _pendingInstallIds.Remove(i.UniqueID); }
            await AfterModsChangedAsync(result.Installed.Select(i => i.UniqueID));
            Status = Loc.F("Instalado desde Nexus: {0}.", string.Join(", ", result.Installed.Select(i => i.Name)));
            if (_nexusQueue.Count > 0) await OpenNextNexusAsync(Status);
        });
    }

    // ---------------------------------------------------------------- profiles

    void ReloadProfiles()
    {
        var selectedId = SelectedProfile?.Profile.Id;
        Profiles.Clear();
        foreach (var p in _profiles.Active) Profiles.Add(new ProfileItem(p, p.Id == _settings.ActiveProfileId, SyncBadge(p)));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == selectedId) ?? Profiles.FirstOrDefault(p => p.IsActive);
        _suppressProfileSwitch = true;
        try { ActiveProfileChoice = Profiles.FirstOrDefault(p => p.IsActive); }
        finally { _suppressProfileSwitch = false; }
        OnPropertyChanged(nameof(NoProfiles));
        OnPropertyChanged(nameof(HasActiveProfile));
        ActiveProfileText = ActiveProfile is { } a ? Loc.F("Perfil: {0}", a.Name) : Loc.T("Sin perfil activo");
    }

    [RelayCommand]
    async Task CreateProfileAsync()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) { Status = Loc.T("Escribe un nombre para el perfil."); return; }
        var p = _profiles.Create(name, _installed);
        if (PerProfileConfigs) _configStore.Capture(p, _installed);
        _settings.ActiveProfileId = p.Id; _settings.Save();
        NewProfileName = "";
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Profile.Id == p.Id);
        await RefreshModsAsync();
        Status = Loc.F("Perfil «{0}» creado con los {1} mods actuales y activado.", name, p.Mods.Count);
        _ = SyncIfConfiguredAsync();
    }

    [RelayCommand]
    async Task ActivateProfileAsync()
    {
        if (SelectedProfile is not { } sel || !GameFound) return;
        await RunAsync(Loc.F("Activando «{0}»…", sel.Name), () => ApplyProfileAsync(sel.Profile));
    }

    async Task ApplyProfileAsync(Profile profile)
    {
        _installed = await Task.Run(() => ModScanner.Scan(ModsFolder));
        // Leaving a profile: keep its mod settings (the game or the editor may have changed them since the last capture).
        if (PerProfileConfigs && ActiveProfile is { } leaving && await Task.Run(() => _configStore.Capture(leaving, _installed)) > 0)
            _profiles.MarkChanged(leaving);
        _settings.ActiveProfileId = profile.Id; _settings.Save();
        var plan = ProfileService.Plan(profile, _installed);
        var toggled = await Task.Run(() => ProfileService.ApplyToggles(ModsFolder, plan));

        _pendingInstallIds.Clear();
        foreach (var m in plan.ToInstall) _pendingInstallIds.Add(m.UniqueID);
        foreach (var old in Updates.Where(u => u.IsNew).ToList()) Updates.Remove(old);
        MissingFromProfile.Clear();
        _nexusQueue.Clear();

        var installedNow = 0;
        if (plan.ToInstall.Count > 0)
        {
            Status = Loc.F("Buscando dónde descargar los {0} mods que faltan…", plan.ToInstall.Count);
            var svc = new UpdateService(_http, await _gh.GetValidTokenAsync());
            List<ModUpdate> found;
            try { found = await svc.ResolveMissingAsync(plan.ToInstall.Select(m => (m.UniqueID, m.Name, (IReadOnlyList<string>)m.UpdateKeys))); }
            catch (Exception ex) { found = new(); MissingFromProfile.Add(Loc.T("No se pudo consultar smapi.io: ") + ex.Message); }

            foreach (var m in plan.ToInstall.Where(m => found.All(f => !string.Equals(f.UniqueID, m.UniqueID, StringComparison.OrdinalIgnoreCase))))
                MissingFromProfile.Add(Loc.F("«{0}»: no se encontró dónde descargarlo (instálalo a mano o búscalo en Explorar).", m.Name));

            var autos = found.Where(f => f.Method == UpdateMethod.Auto).ToList();
            for (var i = 0; i < autos.Count; i++)
            {
                var u = autos[i];
                Status = Loc.F("Instalando «{0}» ({1}/{2})…", u.Name, i + 1, autos.Count);
                try { await InstallAutoCoreAsync(u); installedNow++; }
                catch (Exception ex)
                {
                    MissingFromProfile.Add(Loc.F("«{0}»: falló la descarga automática ({1}).", u.Name, ex.Message));
                    Updates.Add(new UpdateItem(u, true, ActOnUpdateAsync));
                }
            }
            foreach (var u in found.Where(f => f.Method != UpdateMethod.Auto)) Updates.Add(new UpdateItem(u, true, ActOnUpdateAsync));
        }

        if (installedNow > 0)
        {
            // Freshly installed mods arrive enabled; make them match the profile.
            _installed = await Task.Run(() => ModScanner.Scan(ModsFolder));
            await Task.Run(() => ProfileService.ApplyToggles(ModsFolder, ProfileService.Plan(profile, _installed)));
        }
        var configsApplied = 0;
        if (PerProfileConfigs)
        {
            var applied = await Task.Run(() => { var w = _configStore.Apply(profile, _installed, out var a); return (Written: w, Adopted: a); });
            configsApplied = applied.Written;
            if (applied.Adopted > 0) _profiles.MarkChanged(profile);   // the profile now carries settings it did not have before
        }
        await RefreshModsAsync();
        ReloadProfiles();

        var waiting = Updates.Count(u => u.IsNew);
        if (waiting > 0) SelectedTab = TabUpdates;
        var summary = Loc.F("Perfil «{0}» aplicado: {1} cambios de activación, {2} mods instalados", profile.Name, toggled, installedNow);
        if (waiting > 0) summary += Loc.F(", {0} esperan un clic en Nexus/web (pestaña Actualizaciones)", waiting);
        if (configsApplied > 0) summary += Loc.F(", {0} configuraciones restauradas", configsApplied);
        summary += MissingFromProfile.Count > 0 ? Loc.F(", {0} sin resolver (ver Perfiles).", MissingFromProfile.Count) : ".";
        Status = summary;
    }

    // ---------------------------------------------------------------- Nexus one-by-one queue

    readonly Queue<int> _nexusQueue = new();

    [RelayCommand]
    async Task InstallNexusQueueAsync()
    {
        var ids = Updates.Where(u => u.Update.Method == UpdateMethod.NexusBrowser && u.Update.NexusModId is not null)
            .Select(u => u.Update.NexusModId!.Value).Distinct().ToList();
        if (ids.Count == 0) { Status = Loc.T("No hay mods de Nexus pendientes."); return; }
        _nexusQueue.Clear();
        foreach (var id in ids) _nexusQueue.Enqueue(id);
        await OpenNextNexusAsync(Loc.F("{0} mods de Nexus en cola.", ids.Count));
    }

    async Task OpenNextNexusAsync(string prefix)
    {
        while (_nexusQueue.Count > 0)
        {
            var id = _nexusQueue.Dequeue();
            if (!Updates.Any(u => u.Update.NexusModId == id)) continue; // already installed meanwhile
            await OpenNexusPageAsync(id);
            Status = Loc.F("{0} Siguiente: pulsa «Descarga lenta / Mod Manager» en Nexus (quedan {1} después).", prefix, _nexusQueue.Count);
            return;
        }
        Status = prefix + Loc.T(" Cola de Nexus terminada.");
    }

    [RelayCommand]
    void RenameProfile()
    {
        if (SelectedProfile is not { } sel || string.IsNullOrWhiteSpace(RenameText)) return;
        _profiles.Rename(sel.Profile, RenameText.Trim());
        ReloadProfiles();
        _ = SyncIfConfiguredAsync();
    }

    [RelayCommand]
    void DuplicateProfile()
    {
        if (SelectedProfile is not { } sel) return;
        var copy = _profiles.Duplicate(sel.Profile, sel.Name + Loc.T(" (copia)"));
        _configStore.Copy(sel.Profile.Id, copy.Id);
        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == copy.Id);
        _ = SyncIfConfiguredAsync();
    }

    [RelayCommand]
    async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } sel) return;
        _profiles.Delete(sel.Profile);
        _configStore.Delete(sel.Profile.Id);
        if (_settings.ActiveProfileId == sel.Profile.Id) { _settings.ActiveProfileId = null; _settings.Save(); }
        ReloadProfiles();
        await RefreshModsAsync();
        Status = Loc.F("Perfil «{0}» eliminado (los mods instalados no se tocan).", sel.Name);
        _ = SyncIfConfiguredAsync();
    }

    [RelayCommand]
    async Task SaveCurrentToProfileAsync()
    {
        if (SelectedProfile is not { } sel) return;
        _profiles.Capture(sel.Profile, _installed);
        ReloadProfiles();
        await RefreshModsAsync();
        Status = Loc.F("Perfil «{0}» actualizado con los mods actuales.", sel.Name);
        _ = SyncIfConfiguredAsync();
    }

    // ---------------------------------------------------------------- sync

    [RelayCommand]
    async Task SyncNowAsync()
    {
        if (!SyncConfigured) { Status = Loc.T("Configura la sincronización en Ajustes."); SelectedTab = TabSettings; return; }
        await SyncAsync(silent: false);
    }

    // ---------------------------------------------------------------- settings

    [RelayCommand]
    async Task BrowseGameAsync()
    {
        if (PickFolder is null) return;
        var f = await PickFolder();
        if (f is not null) GamePath = f;
    }

    [RelayCommand]
    void DetectGame()
    {
        var f = GameLocator.Find();
        if (f is null) { Status = Loc.T("No se encontró Stardew Valley automáticamente; elige la carpeta a mano."); return; }
        GamePath = f;
        Status = Loc.T("Juego encontrado.");
    }

    [RelayCommand]
    async Task BrowseSyncFolderAsync()
    {
        if (PickFolder is null) return;
        var f = await PickFolder();
        if (f is not null) SyncFolder = f;
    }

    [RelayCommand]
    async Task SaveSettingsAsync()
    {
        _settings.GamePath = GamePath.Trim();
        _settings.NexusApiKey = NexusApiKey.Trim();
        var typedToken = GitHubToken.Trim();
        if (typedToken != (_gh.AccessToken ?? "")) _gh.SetTokens(typedToken.Length == 0 ? null : new GitHubTokens(typedToken, null, null)); // a pasted token has no refresh token
        GitHubLoggedIn = _gh.IsLoggedIn;
        _settings.SyncFolder = SyncFolder.Trim();
        _settings.SyncMode = (SyncMode)SyncModeIndex;
        _settings.SyncOnStartup = SyncOnStartup;
        _settings.PerProfileConfigs = PerProfileConfigs;
        _settings.SyncAuto = SyncAuto;
        SaveLanguageAndMode();
        _settings.Save();
        Status = Loc.T("Ajustes guardados.");
        await RefreshModsAsync();
    }

    [RelayCommand]
    async Task ValidateNexusKeyAsync()
    {
        await RunAsync(Loc.T("Comprobando la clave de Nexus…"), async () =>
        {
            var user = await new NexusClient(_http, NexusApiKey.Trim()).ValidateAsync();
            Status = user is null ? Loc.T("La clave de Nexus no es válida.") : Loc.F("Clave válida: sesión de «{0}».", user);
        });
    }

    [RelayCommand]
    void RegisterNxm()
    {
        try { NxmRegistration.Register(); Status = Loc.T("Listo: los botones «Mod Manager Download» de Nexus abrirán esta aplicación."); }
        catch (Exception ex) { Status = Loc.T("No se pudo registrar: ") + ex.Message; }
    }

    // ---------------------------------------------------------------- SMAPI + play

    [RelayCommand]
    async Task InstallSmapiAsync()
    {
        if (!GameFound) return;
        await RunAsync(Loc.T("Descargando SMAPI…"), async () =>
        {
            var installer = new SmapiInstaller(_http);
            var release = await installer.LatestAsync() ?? throw new InvalidOperationException(Loc.T("No se pudo consultar la última versión de SMAPI."));
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => { ProgressIndeterminate = p < 0; Progress = Math.Max(p, 0) * 100; }));
            var dir = await installer.PrepareAsync(release, AppPaths.CacheDir, progress);

            ProgressIndeterminate = true;
            Status = Loc.T("Se abrió el instalador oficial de SMAPI en otra ventana. Sigue sus pasos; al terminar se cerrará solo.");
            await SmapiInstaller.RunAsync(dir);

            UpdateGameState();
            await RefreshModsAsync();
            await RefreshSmapiAsync(queryLatest: false);
            Status = SmapiMissing
                ? Loc.T("El instalador terminó pero SMAPI aún no aparece en la carpeta del juego. ¿Es la carpeta correcta?")
                : SmapiNeedsAction
                    ? Loc.T("El instalador terminó, pero la comprobación detecta un problema: ") + SmapiInfoText
                    : Loc.F("Comprobado: {0} Ya puedes jugar.", SmapiInfoText);
        });
    }

    [RelayCommand]
    void Play()
    {
        try { GameLauncher.Play(GamePath); Status = Loc.T("Iniciando Stardew Valley con SMAPI…"); }
        catch (Exception ex) { Status = Loc.T("No se pudo iniciar el juego: ") + ex.Message; }
    }

    // ---------------------------------------------------------------- GitHub sign-in (device flow)

    /// <param name="mode">"browser": open the GitHub page here; "qr": show a QR code to scan with a phone.</param>
    [RelayCommand]
    async Task GitHubLoginAsync(string? mode)
    {
        if (!GitHubLoginAvailable || LoginInProgress) return;
        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;
        LoginInProgress = true;
        try
        {
            var flow = new GitHubDeviceFlow(_http, Branding.EffectiveGitHubClientId);
            var code = await flow.StartAsync(ct);
            LoginCode = code.UserCode;
            LoginUrl = code.VerificationUri;
            if (mode == "qr")
            {
                LoginQr = ImageCache.Qr(code.VerificationUri);
                Status = Loc.F("Escanea el QR con el móvil, escribe el código {0} y pulsa Autorizar.", code.UserCode);
            }
            else
            {
                Status = Loc.F("En la página de GitHub que se acaba de abrir, escribe el código {0} y pulsa Autorizar.", code.UserCode);
                Shell.OpenUrl(code.VerificationUri);
            }

            _gh.SetTokens(await flow.WaitForTokensAsync(code, ct));   // saves the refresh token and expiry too
            SyncModeIndex = (int)SyncMode.Gist;
            _settings.SyncMode = SyncMode.Gist;
            _settings.Save();
            Status = Loc.T("Sesión de GitHub iniciada.");
            await SyncAsync(silent: false);
        }
        catch (OperationCanceledException) { Status = Loc.T("Inicio de sesión cancelado."); }
        catch (Exception ex) { Status = Loc.T("No se pudo iniciar sesión: ") + ex.Message; }
        finally { LoginInProgress = false; LoginCode = ""; LoginUrl = ""; LoginQr = null; _loginCts = null; }
    }

    [RelayCommand] void CancelLogin() => _loginCts?.Cancel();

    [RelayCommand]
    void GitHubLogout()
    {
        _gh.Clear();
        Status = Loc.T("Sesión de GitHub cerrada en este dispositivo (tus perfiles siguen en tu Gist).");
    }

    [RelayCommand] void OpenNexusKeyPage() => Shell.OpenUrl("https://www.nexusmods.com/settings/api-keys");
    [RelayCommand] void OpenGitHubTokenPage() => Shell.OpenUrl("https://github.com/settings/tokens/new?scopes=gist&description=Junimo%20Manager");
    [RelayCommand] void OpenSmapiPage() => Shell.OpenUrl("https://smapi.io/");
}
