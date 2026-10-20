using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.Core.Localization;
using SdvMods.Core.Profiles;
using SdvMods.Core.Settings;
using SdvMods.Core.Sync;

namespace SdvMods.App.ViewModels;

// Profile synchronisation: explicit check / upload / download, an optional automatic upload, conflicts and per-profile status.
public partial class MainViewModel
{
    SyncPlan? _plan;
    CancellationTokenSource? _autoSyncCts;
    bool _syncRunning;
    readonly string _device = Environment.MachineName;

    [ObservableProperty] public partial string SyncStatusText { get; set; } = "";
    [ObservableProperty] public partial bool SyncAuto { get; set; } = true;

    public bool SyncVisible => SyncConfigured;
    public bool SyncHasConflicts => _plan?.Count(SyncState.Conflict) > 0;

    ProfileSyncEngine Engine => new(_profiles, _configStore, _device);

    IRemoteProfileStore CreateRemote(out GistProfileStore? gist)
    {
        gist = null;
        if ((SyncMode)SyncModeIndex == SyncMode.Gist) return gist = new GistProfileStore(_http, _gh.AccessToken ?? "", _settings.GistId);
        return new FolderProfileStore(SyncFolder.Trim());
    }

    /// <summary>Whenever the GitHub login changes (sign-in, sign-out, automatic renewal) keep it on disk and in the Settings fields.</summary>
    void SaveGitHubSession()
    {
        _settings.GitHubToken = _gh.AccessToken;
        _settings.GitHubRefreshToken = _gh.RefreshToken;
        _settings.GitHubTokenExpiresAt = _gh.ExpiresAt;
        _settings.Save();
        GitHubToken = _gh.AccessToken ?? "";
        GitHubLoggedIn = _gh.IsLoggedIn;
        RefreshSyncStatus();
    }

    /// <summary>GitHub no longer accepts this login and it cannot be renewed: forget it and ask for a new sign-in.</summary>
    void GitHubLoginLost()
    {
        _gh.Clear();
        Status = Loc.T("La sesión de GitHub caducó o fue revocada. Vuelve a iniciar sesión en Ajustes.");
        SyncStatusText = "☁ ⚠ " + Loc.T("inicia sesión de nuevo en Ajustes");
    }

    static bool IsUnauthorized(Exception ex) => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized };

    void RememberGist(GistProfileStore? gist)
    {
        if (gist?.GistId is { } id && id != _settings.GistId) { _settings.GistId = id; _settings.Save(); }
    }

    // ---------------------------------------------------------------- status shown to the user

    string SyncBadge(Profile p)
    {
        if (!SyncConfigured) return "";
        if (!p.SyncEnabled) return Loc.T("solo aquí");
        var item = _plan?.Find(p.Id);
        var pending = p.BaseRev is null ? !p.Deleted : p.Rev != p.BaseRev;
        return item?.State switch
        {
            SyncState.Conflict => "⚠ " + Loc.T("conflicto"),
            SyncState.Pull => "↓ " + Loc.T("por bajar"),
            SyncState.Push => "↑ " + Loc.T("por subir"),
            _ when pending => "↑ " + Loc.T("por subir"),
            SyncState.UpToDate => "☁ ✓",
            _ => "",
        };
    }

    void RefreshSyncStatus()
    {
        OnPropertyChanged(nameof(SyncVisible));
        OnPropertyChanged(nameof(SyncHasConflicts));
        if (!SyncConfigured) { SyncStatusText = ""; return; }
        var conflicts = _plan?.Count(SyncState.Conflict) ?? 0;
        var down = _plan?.Count(SyncState.Pull) ?? 0;
        var up = Engine.PendingLocalChanges();
        SyncStatusText = conflicts > 0 ? "☁ ⚠ " + Loc.F("{0} perfiles en conflicto", conflicts)
            : up > 0 ? "☁ ↑ " + Loc.F("{0} perfiles por subir", up)
            : down > 0 ? "☁ ↓ " + Loc.F("{0} perfiles por bajar", down)
            : _plan is null ? "☁ " + Loc.T("sin comprobar")
            : "☁ ✓ " + Loc.F("al día ({0:t})", _plan.CheckedAt);
        foreach (var item in Profiles) item.SyncText = SyncBadge(item.Profile);
        NotifySelectedProfileSync();
    }

    // ---------------------------------------------------------------- the selected profile's sync controls

    public bool SelectedProfileSyncEnabled
    {
        get => SelectedProfile?.Profile.SyncEnabled ?? true;
        set
        {
            if (SelectedProfile is not { } sel || sel.Profile.SyncEnabled == value) return;
            sel.Profile.SyncEnabled = value;
            _profiles.Save();
            RefreshSyncStatus();
            OnPropertyChanged();
        }
    }

    public bool SelectedProfileInConflict => SelectedProfile is { } s && _plan?.Find(s.Profile.Id)?.State == SyncState.Conflict;

    void NotifySelectedProfileSync()
    {
        OnPropertyChanged(nameof(SelectedProfileSyncEnabled));
        OnPropertyChanged(nameof(SelectedProfileInConflict));
        OnPropertyChanged(nameof(SelectedProfileSyncText));
    }

    public string SelectedProfileSyncText => SelectedProfile is { } s ? (SyncBadge(s.Profile) is { Length: > 0 } b ? b : Loc.T("sin comprobar")) : "";

    // ---------------------------------------------------------------- running a sync

    /// <summary>Called after every local change: with automatic upload on, the changes go up a few seconds later (one batch, no matter how many edits).</summary>
    Task SyncIfConfiguredAsync()
    {
        RefreshSyncStatus();
        if (!SyncConfigured || !SyncAuto) return Task.CompletedTask;
        _autoSyncCts?.Cancel();
        var cts = _autoSyncCts = new CancellationTokenSource();
        return AutoPushAfterPauseAsync(cts.Token);
    }

    async Task AutoPushAfterPauseAsync(CancellationToken ct)
    {
        try { await Task.Delay(4000, ct); } catch (OperationCanceledException) { return; }
        await RunSyncAsync(SyncDirection.PushOnly, announce: false, askConflicts: false);
    }

    Task SyncAsync(bool silent) => RunSyncAsync(SyncDirection.Both, announce: !silent, askConflicts: !silent);

    [RelayCommand]
    async Task CheckSyncAsync()
    {
        if (!SyncConfigured) { Status = Loc.T("Configura la sincronización en Ajustes."); SelectedTab = TabSettings; return; }
        await RunSyncAsync(SyncDirection.Both, announce: true, askConflicts: false, checkOnly: true);
    }

    [RelayCommand]
    async Task PushSyncAsync()
    {
        if (!SyncConfigured) { Status = Loc.T("Configura la sincronización en Ajustes."); SelectedTab = TabSettings; return; }
        await RunSyncAsync(SyncDirection.PushOnly, announce: true, askConflicts: true);
    }

    [RelayCommand]
    async Task PullSyncAsync()
    {
        if (!SyncConfigured) { Status = Loc.T("Configura la sincronización en Ajustes."); SelectedTab = TabSettings; return; }
        await RunSyncAsync(SyncDirection.PullOnly, announce: true, askConflicts: true);
    }

    async Task RunSyncAsync(SyncDirection direction, bool announce, bool askConflicts, bool checkOnly = false)
    {
        if (!SyncConfigured) { RefreshSyncStatus(); return; }
        if (_syncRunning) return;
        _syncRunning = true;
        try
        {
        var retry = true;
        while (true)
        try
        {
            if ((SyncMode)SyncModeIndex == SyncMode.Gist && await _gh.GetValidTokenAsync() is null) { GitHubLoginLost(); break; }
            var remote = CreateRemote(out var gist);
            var engine = Engine;
            var plan = await engine.CheckAsync(remote);
            RememberGist(gist);

            var report = checkOnly ? new SyncReport() : await engine.ApplyAsync(plan, remote, direction);
            _plan = new SyncPlan
            {
                CheckedAt = DateTimeOffset.Now,
                Items = plan.Items.Select(i => report.Pushed.Contains(i.Id) || report.Pulled.Contains(i.Id) ? i with { State = SyncState.UpToDate } : i).ToList(),
            };
            ReloadProfiles();
            RefreshSyncStatus();
            await ReapplyActiveIfPulledAsync(report);

            if (announce)
            {
                var conflicts = _plan.Count(SyncState.Conflict);
                Status = checkOnly
                    ? Loc.F("Comprobado con {0}: {1} por subir, {2} por bajar, {3} en conflicto.", remote.Name, engine.PendingLocalChanges(), _plan.Count(SyncState.Pull), conflicts)
                    : Loc.F("Sincronizado con {0}: {1} subidos, {2} bajados", remote.Name, report.Pushed.Count, report.Pulled.Count)
                      + (conflicts > 0 ? Loc.F(", {0} en conflicto.", conflicts) : ".");
            }
            break;
        }
        catch (Exception ex) when (IsUnauthorized(ex) && (SyncMode)SyncModeIndex == SyncMode.Gist)
        {
            // The token was rejected: renew it once and try again; if that is not possible the login is gone.
            if (retry) { retry = false; if (await _gh.TryRefreshAsync()) continue; }
            GitHubLoginLost();
            break;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = Loc.T("No se pudo sincronizar: ") + ex.Message;
            SyncStatusText = "☁ ⚠ " + Loc.T("error al sincronizar");
            break;
        }
        }
        finally { _syncRunning = false; }

        if (askConflicts)
            foreach (var id in (_plan?.Items.Where(i => i.State == SyncState.Conflict).Select(i => i.Id) ?? Enumerable.Empty<string>()).ToList())
                if (!await ResolveConflictAsync(id)) break;
    }

    /// <summary>If another device changed the profile that is active here, bring this PC in line with it.</summary>
    async Task ReapplyActiveIfPulledAsync(SyncReport report)
    {
        var activeId = _settings.ActiveProfileId;
        if (activeId is null || !report.Pulled.Contains(activeId) || !GameFound) return;
        var active = _profiles.Document.Profiles.FirstOrDefault(p => p.Id == activeId);
        if (active is null || active.Deleted)
        {
            _settings.ActiveProfileId = null; _settings.Save();
            ReloadProfiles();
            await RefreshModsAsync();
            Status = Loc.T("El perfil activo se eliminó en otro dispositivo.");
            return;
        }
        var wasBusy = IsBusy;
        if (!wasBusy) IsBusy = true;
        try { await ApplyProfileAsync(active); } finally { if (!wasBusy) IsBusy = false; }
        Status = Loc.F("Sincronizado: el perfil «{0}» cambió en otro dispositivo y se aplicó aquí.", active.Name);
    }

    // ---------------------------------------------------------------- conflicts

    [RelayCommand]
    async Task ResolveSelectedConflictAsync()
    {
        if (SelectedProfile is { } s) await ResolveConflictAsync(s.Profile.Id);
    }

    static IEnumerable<string> DescribeDifferences(Profile local, RemoteProfile remote)
    {
        if (local.Name != remote.Name) yield return Loc.F("Nombre: «{0}» aquí, «{1}» en el otro dispositivo", local.Name, remote.Name);
        var mine = local.Mods.ToDictionary(m => m.UniqueID, StringComparer.OrdinalIgnoreCase);
        var theirs = remote.Mods.ToDictionary(m => m.UniqueID, StringComparer.OrdinalIgnoreCase);
        foreach (var m in mine.Values.Where(m => !theirs.ContainsKey(m.UniqueID))) yield return Loc.F("Solo aquí: {0}", m.Name);
        foreach (var m in theirs.Values.Where(m => !mine.ContainsKey(m.UniqueID))) yield return Loc.F("Solo en el otro: {0}", m.Name);
        foreach (var m in mine.Values.Where(m => theirs.TryGetValue(m.UniqueID, out var t) && (t.Version != m.Version || t.Enabled != m.Enabled)))
        {
            var t = theirs[m.UniqueID];
            yield return Loc.F("Distinto: {0} ({1} {2} aquí; {3} {4} en el otro)", m.Name, m.Version, m.Enabled ? Loc.T("activado") : Loc.T("desactivado"), t.Version, t.Enabled ? Loc.T("activado") : Loc.T("desactivado"));
        }
        if (local.Deleted != remote.Deleted) yield return remote.Deleted ? Loc.T("En el otro dispositivo se eliminó este perfil") : Loc.T("Aquí se eliminó este perfil");
    }

    /// <summary>Asks which version to keep. Returns false if the user chose to decide later.</summary>
    async Task<bool> ResolveConflictAsync(string profileId)
    {
        var local = _profiles.Document.Profiles.FirstOrDefault(p => p.Id == profileId);
        var remoteItem = _plan?.Find(profileId)?.Remote;
        if (local is null || remoteItem is null) return true;

        var diff = DescribeDifferences(local, remoteItem).ToList();
        if (diff.Count == 0) diff.Add(Loc.T("La diferencia está en la configuración de los mods."));
        var choice = await AskAsync(
            Loc.F("Conflicto en «{0}»", local.Name),
            Loc.F("Este perfil cambió aquí y en otro dispositivo ({0}, {1:g}) desde la última sincronización. Elige qué versión conservar; la otra no se pierde del todo si guardas la tuya como copia.", string.IsNullOrEmpty(remoteItem.Device) ? "?" : remoteItem.Device, remoteItem.UpdatedAt.ToLocalTime()),
            diff,
            Loc.T("Quedarme con la del otro (guardo la mía como copia)"),
            Loc.T("Quedarme con la mía (reemplaza la del otro)"));
        if (choice == 0) return false;

        try
        {
            await _gh.GetValidTokenAsync();
            var remote = CreateRemote(out var gist);
            var copyId = await Engine.ResolveAsync(profileId, choice == 1 ? ConflictChoice.KeepRemote : ConflictChoice.KeepLocal, remote);
            RememberGist(gist);
            ReloadProfiles();
            if (choice == 1)
            {
                if (_settings.ActiveProfileId == profileId && GameFound && _profiles.Find(profileId) is { } active)
                {
                    var wasBusy = IsBusy;
                    if (!wasBusy) IsBusy = true;
                    try { await ApplyProfileAsync(active); } finally { if (!wasBusy) IsBusy = false; }
                }
                Status = copyId is null ? Loc.T("Conflicto resuelto.") : Loc.F("Conflicto resuelto: se conservó la versión del otro dispositivo y tu versión quedó como «{0}».", _profiles.Find(copyId)?.Name ?? "");
            }
            else Status = Loc.T("Conflicto resuelto: tu versión sustituyó a la del otro dispositivo.");
            _plan = new SyncPlan { CheckedAt = DateTimeOffset.Now, Items = (_plan?.Items ?? Array.Empty<SyncItem>()).Select(i => i.Id == profileId ? i with { State = SyncState.UpToDate } : i).ToList() };
            RefreshSyncStatus();
            _ = SyncIfConfiguredAsync();
        }
        catch (Exception ex)
        {
            Status = Loc.T("No se pudo resolver el conflicto: ") + ex.Message;
            return false;
        }
        return true;
    }
}
