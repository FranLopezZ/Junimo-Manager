using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.Core.Localization;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;

namespace SdvMods.App.ViewModels;

// Main "Mods" tab: profile switcher, search / scope filter, multi-selection and batch actions.
public partial class MainViewModel
{
    readonly List<ModItem> _allMods = new();
    bool _suppressProfileSwitch;

    public bool HasActiveProfile => ActiveProfile is not null;
    public bool NoProfiles => Profiles.Count == 0;

    [ObservableProperty] public partial string ModFilterText { get; set; } = "";
    /// <summary>0 = only the active profile's mods, 1 = everything installed.</summary>
    [ObservableProperty] public partial int ModScopeIndex { get; set; }
    [ObservableProperty] public partial ProfileItem? ActiveProfileChoice { get; set; }
    [ObservableProperty] public partial bool ConfirmBatchUninstall { get; set; }

    partial void OnModFilterTextChanged(string value) => ApplyModFilter();
    partial void OnModScopeIndexChanged(int value) => ApplyModFilter();
    partial void OnConfirmBatchUninstallChanged(bool value) => OnPropertyChanged(nameof(BatchUninstallText));

    // ---------------------------------------------------------------- list, filter, selection

    public int SelectedCount => _allMods.Count(m => m.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionText => Loc.F("{0} seleccionados", SelectedCount);
    public string BatchUninstallText => ConfirmBatchUninstall ? Loc.F("¿Seguro? ({0})", SelectedCount) : Loc.T("Desinstalar");

    public bool AllVisibleSelected
    {
        get => Mods.Count > 0 && Mods.All(m => m.IsSelected);
        set
        {
            foreach (var m in Mods) m.IsSelected = value;
            OnPropertyChanged();
        }
    }

    void OnModItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModItem.IsSelected)) return;
        ConfirmBatchUninstall = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(BatchUninstallText));
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    void ApplyModFilter()
    {
        var q = ModFilterText.Trim();
        var onlyProfile = HasActiveProfile && ModScopeIndex == 0;
        bool Matches(ModItem m) => (!onlyProfile || m.InProfile) &&
            (q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             m.Author.Contains(q, StringComparison.OrdinalIgnoreCase) || m.Mod.UniqueID.Contains(q, StringComparison.OrdinalIgnoreCase));

        var visible = _allMods.Where(Matches).ToList();
        foreach (var hidden in _allMods.Except(visible)) hidden.IsSelected = false; // never act on mods you cannot see
        Mods.Clear();
        foreach (var m in visible) Mods.Add(m);
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    [RelayCommand] void ClearSelection() { foreach (var m in _allMods) m.IsSelected = false; }

    // ---------------------------------------------------------------- batch actions

    enum BatchOp { Enable, Disable, RemoveFromProfile, AddToProfile, Uninstall }

    List<ModItem> Selected => _allMods.Where(m => m.IsSelected).ToList();

    [RelayCommand] Task EnableSelectedAsync() => BatchAsync(Selected, BatchOp.Enable);
    [RelayCommand] Task DisableSelectedAsync() => BatchAsync(Selected, BatchOp.Disable);
    [RelayCommand] Task RemoveSelectedFromProfileAsync() => BatchAsync(Selected, BatchOp.RemoveFromProfile);
    [RelayCommand] Task AddSelectedToProfileAsync() => BatchAsync(Selected, BatchOp.AddToProfile);
    [RelayCommand] Task EnableAllAsync() => BatchAsync(Mods.ToList(), BatchOp.Enable);
    [RelayCommand] Task DisableAllAsync() => BatchAsync(Mods.ToList(), BatchOp.Disable);

    [RelayCommand]
    async Task UninstallSelectedAsync()
    {
        if (!HasSelection) return;
        if (!ConfirmBatchUninstall)
        {
            ConfirmBatchUninstall = true;
            Status = Loc.F("Se borrarán del disco {0} mods. Pulsa otra vez para confirmar.", SelectedCount);
            return;
        }
        await BatchAsync(Selected, BatchOp.Uninstall);
    }

    /// <summary>Same as pressing the card's Enable/Disable button (used by tests).</summary>
    public Task ToggleTargetAsync(string modName)
        => ToggleModAsync(_allMods.Single(m => m.Name == modName));

    Task ToggleMembershipAsync(ModItem item) => BatchAsync(new List<ModItem> { item }, item.InProfile ? BatchOp.RemoveFromProfile : BatchOp.AddToProfile);

    /// <summary>
    /// Before turning mods off or removing them, offers to include the active mods that need them; before turning mods on,
    /// offers to also turn on the disabled mods they need. Returns the final list, or null if the user cancelled.
    /// </summary>
    async Task<List<ModItem>?> ResolveRelatedAsync(List<ModItem> items, BatchOp op)
    {
        if (op is BatchOp.AddToProfile) return items;
        var ids = items.Where(i => op == BatchOp.Enable || i.Enabled).Select(i => i.Mod.UniqueID).ToList();
        if (ids.Count == 0) return items;

        List<InstalledMod> related;
        string title, message, primary;
        if (op == BatchOp.Enable)
        {
            related = ModScanner.FindDisabledRequirements(_installed, ids);
            title = Loc.T("Faltan mods por activar");
            message = Loc.T("Para funcionar, lo que vas a activar necesita estos mods, que están desactivados:");
            primary = Loc.T("Activar también esos mods");
        }
        else
        {
            related = ModScanner.FindDependants(_installed, ids);
            title = Loc.T("Otros mods dependen de esto");
            (message, primary) = op switch
            {
                BatchOp.Uninstall => (Loc.T("Estos mods activos necesitan lo que vas a desinstalar y dejarían de funcionar:"), Loc.T("Desinstalar también esos mods")),
                BatchOp.RemoveFromProfile => (Loc.T("Estos mods activos necesitan lo que vas a quitar del perfil y dejarían de funcionar:"), Loc.T("Quitar también esos mods")),
                _ => (Loc.T("Estos mods activos necesitan lo que vas a desactivar y dejarían de funcionar:"), Loc.T("Desactivar también esos mods")),
            };
        }
        related = related.Where(r => items.All(i => !string.Equals(i.Mod.UniqueID, r.UniqueID, StringComparison.OrdinalIgnoreCase))).ToList();
        if (related.Count == 0) return items;

        var choice = await AskAsync(title, message, related.Select(r => $"{r.Name}  {r.Version}"), primary, Loc.T("Solo lo seleccionado"));
        if (choice == 0) { Status = Loc.T("Cancelado."); return null; }
        if (choice == 2) return items;
        var extra = _allMods.Where(m => related.Any(r => string.Equals(r.UniqueID, m.Mod.UniqueID, StringComparison.OrdinalIgnoreCase))).ToList();
        return items.Concat(extra).ToList();
    }

    async Task BatchAsync(List<ModItem> items, BatchOp op)
    {
        if (items.Count == 0 || !GameFound || IsBusy) return;
        if (await ResolveRelatedAsync(items, op) is not { } all) return;
        items = all;
        var working = op switch
        {
            BatchOp.Enable => Loc.T("Activando…"),
            BatchOp.Disable => Loc.T("Desactivando…"),
            BatchOp.Uninstall => Loc.T("Desinstalando…"),
            _ => Loc.T("Actualizando el perfil…"),
        };
        await RunAsync(working, async () =>
        {
            var profile = ActiveProfile;
            var folder = ModsFolder;
            var installer = Installer;
            await Task.Run(() =>
            {
                foreach (var it in items)
                {
                    switch (op)
                    {
                        case BatchOp.Enable: if (!it.Enabled) ModScanner.SetEnabled(folder, it.Mod, true); break;
                        case BatchOp.Disable: if (it.Enabled) ModScanner.SetEnabled(folder, it.Mod, false); break;
                        case BatchOp.RemoveFromProfile: if (it.Enabled) ModScanner.SetEnabled(folder, it.Mod, false); break; // out of the profile = does not load
                        case BatchOp.Uninstall: installer.Uninstall(it.Mod); break;
                    }
                }
            });

            if (profile is not null)
            {
                foreach (var it in items)
                {
                    switch (op)
                    {
                        case BatchOp.Enable: _profiles.UpsertMod(profile, it.Mod, true); break;
                        case BatchOp.Disable: if (ProfileService.IsMember(profile, it.Mod.UniqueID)) _profiles.UpsertMod(profile, it.Mod, false); break;
                        case BatchOp.AddToProfile: _profiles.UpsertMod(profile, it.Mod, it.Enabled); break;
                        case BatchOp.RemoveFromProfile:
                        case BatchOp.Uninstall: _profiles.RemoveMod(profile, it.Mod.UniqueID); break;
                    }
                }
            }

            ClearSelection();
            await AfterModsChangedAsync();
            Status = items.Count == 1 ? SingleStatus(op, items[0].Name) : op switch
            {
                BatchOp.Enable => Loc.F("{0} mods activados.", items.Count),
                BatchOp.Disable => Loc.F("{0} mods desactivados.", items.Count),
                BatchOp.AddToProfile => Loc.F("{0} mods añadidos al perfil.", items.Count),
                BatchOp.RemoveFromProfile => Loc.F("{0} mods quitados del perfil (siguen en tu carpeta, disponibles para otros perfiles).", items.Count),
                _ => Loc.F("{0} mods desinstalados del disco.", items.Count),
            };
        });
    }

    static string SingleStatus(BatchOp op, string name) => op switch
    {
        BatchOp.Enable => Loc.F("«{0}» activado.", name),
        BatchOp.Disable => Loc.F("«{0}» desactivado.", name),
        BatchOp.AddToProfile => Loc.F("«{0}» añadido al perfil.", name),
        BatchOp.RemoveFromProfile => Loc.F("«{0}» quitado del perfil (sigue en tu carpeta, disponible para otros perfiles).", name),
        _ => Loc.F("«{0}» desinstalado (puedes volver a instalarlo desde Actualizaciones o un perfil).", name),
    };

    // ---------------------------------------------------------------- per-profile mod settings

    /// <summary>
    /// Copies the active profile's mods' config.json files into the profile's store, so changes made in the game (GMCM) or by hand are
    /// not lost when you switch profile. Called at startup, when the window is activated, on close and after editing a config.
    /// </summary>
    public void CaptureActiveConfigs()
    {
        if (!PerProfileConfigs || !GameFound || IsBusy || ActiveProfile is not { } profile) return;
        _captureInFlight = CaptureAsync(profile);
    }

    /// <summary>The background settings capture reads mod files; anything that renames mod folders waits for it first.</summary>
    Task _captureInFlight = Task.CompletedTask;

    async Task CaptureAsync(Profile profile)
    {
        var folder = ModsFolder;
        int changed;
        try { changed = await Task.Run(() => _configStore.Capture(profile, ModScanner.Scan(folder))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; /* a file in use by the game: next time */ }
        if (changed == 0) return;
        _profiles.MarkChanged(profile);   // the profile's settings changed: it has something new to upload
        ReloadProfiles();
        _ = SyncIfConfiguredAsync();
    }

    // ---------------------------------------------------------------- profile bar

    int _switchTicket;

    partial void OnActiveProfileChoiceChanged(ProfileItem? value)
    {
        if (_suppressProfileSwitch || value is null || !GameFound) return;
        if (value.Profile.Id == _settings.ActiveProfileId) return;
        _ = SwitchAfterPauseAsync(value);
    }

    /// <summary>
    /// Scrolling through the selector with the keyboard or a controller passes over several profiles;
    /// only the one you stop on (for a moment) is applied.
    /// </summary>
    async Task SwitchAfterPauseAsync(ProfileItem choice)
    {
        var ticket = ++_switchTicket;
        await Task.Delay(700);
        if (ticket != _switchTicket || !ReferenceEquals(ActiveProfileChoice, choice)) return;
        await SwitchProfileAsync(choice.Profile);
    }

    async Task SwitchProfileAsync(Profile profile)
    {
        try { await RunAsync(Loc.F("Activando «{0}»…", profile.Name), () => ApplyProfileAsync(profile)); }
        finally { ReloadProfiles(); } // if it could not run (busy), the selector goes back to the real active profile
    }

    [RelayCommand]
    async Task CreateEmptyProfileAsync()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) { Status = Loc.T("Escribe un nombre para el perfil."); return; }
        if (!GameFound) return;
        var p = _profiles.CreateEmpty(name);
        NewProfileName = "";
        ReloadProfiles();
        await RunAsync(Loc.F("Activando «{0}»…", name), () => ApplyProfileAsync(p));
        Status = Loc.F("Perfil vacío «{0}» creado y activado: añade mods desde «Todos los instalados» o instala nuevos.", name);
        _ = SyncIfConfiguredAsync();
    }

    [RelayCommand]
    async Task DuplicateActiveProfileAsync()
    {
        if (ActiveProfile is not { } active) return;
        var copy = _profiles.Duplicate(active, active.Name + Loc.T(" (copia)"));
        if (PerProfileConfigs) { _configStore.Capture(active, _installed); _configStore.Copy(active.Id, copy.Id); }
        _settings.ActiveProfileId = copy.Id;
        _settings.Save();
        ReloadProfiles();
        await RefreshModsAsync();
        Status = Loc.F("Perfil «{0}» creado como copia y activado.", copy.Name);
        _ = SyncIfConfiguredAsync();
    }
}
