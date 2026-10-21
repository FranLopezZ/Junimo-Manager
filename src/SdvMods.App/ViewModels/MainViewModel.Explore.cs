using SdvMods.Core.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SdvMods.App.Services;
using SdvMods.Core.Game;
using SdvMods.Core.Mods;
using SdvMods.Core.Updates;

namespace SdvMods.App.ViewModels;

// Explore (Nexus catalog), mod details and the SMAPI status card.
public partial class MainViewModel
{
    // ---------------------------------------------------------------- explore

    static readonly CatalogSort[] SortMap = { CatalogSort.Relevance, CatalogSort.Downloads, CatalogSort.Updated, CatalogSort.Created };
    public ObservableCollection<ExploreItem> ExploreItems { get; } = new();

    [ObservableProperty] public partial string ExploreQuery { get; set; } = "";
    [ObservableProperty] public partial int ExploreSortIndex { get; set; }
    [ObservableProperty] public partial bool ExploreLoading { get; set; }
    [ObservableProperty] public partial string ExploreInfo { get; set; } = "";
    [ObservableProperty] public partial bool CanLoadMore { get; set; }

    int _exploreOffset, _exploreVersion;
    readonly Dictionary<int, NexusModSummary> _nexusInfo = new();

    partial void OnSelectedTabChanged(int value)
    {
        if (value == TabExplore && ExploreItems.Count == 0 && !ExploreLoading) _ = LoadExploreAsync(reset: true);
    }

    partial void OnExploreSortIndexChanged(int value)
    {
        if (SelectedTab == TabExplore) _ = LoadExploreAsync(reset: true);
    }

    [RelayCommand] Task ExploreSearchAsync() => LoadExploreAsync(reset: true);
    [RelayCommand] Task ExploreMoreAsync() => LoadExploreAsync(reset: false);

    HashSet<int> InstalledNexusIds()
        => _installed.Where(m => !ModScanner.IsBundled(m)).Select(m => UpdateKey.NexusId(m.Manifest.UpdateKeys)).Where(i => i is not null).Select(i => i!.Value).ToHashSet();

    async Task LoadExploreAsync(bool reset)
    {
        var version = ++_exploreVersion;
        if (reset) { _exploreOffset = 0; ExploreItems.Clear(); }
        ExploreLoading = true;
        try
        {
            var sort = SortMap[Math.Clamp(ExploreSortIndex, 0, SortMap.Length - 1)];
            var page = await _catalog.SearchAsync(ExploreQuery, sort, null, _exploreOffset, 24);
            if (version != _exploreVersion) return;
            var have = InstalledNexusIds();
            foreach (var m in page.Mods)
            {
                var item = new ExploreItem(m, have.Contains(m.ModId), ShowExploreDetailAsync, InstallExploreAsync);
                ExploreItems.Add(item);
                _ = LoadThumbAsync(item);
            }
            _exploreOffset += page.Mods.Count;
            CanLoadMore = _exploreOffset < page.Total;
            ExploreInfo = page.Total == 0 ? Loc.T("Sin resultados.") : Loc.F("{0} mods en Nexus (sin contenido adulto).", page.Total);
        }
        catch (Exception ex) { if (version == _exploreVersion) ExploreInfo = Loc.T("No se pudo cargar el catálogo: ") + ex.Message; }
        finally { if (version == _exploreVersion) ExploreLoading = false; }
    }

    async Task LoadThumbAsync(ExploreItem item) => item.Thumb = await _images.GetAsync(item.Mod.ThumbnailUrl);

    async Task LoadModThumbsAsync(List<ModItem> items)
    {
        var ids = items.Where(i => i.NexusId is not null).Select(i => i.NexusId!.Value).Distinct().Where(id => !_nexusInfo.ContainsKey(id)).ToList();
        if (ids.Count > 0)
        {
            try { foreach (var kv in await _catalog.GetManyAsync(ids)) _nexusInfo[kv.Key] = kv.Value; }
            catch { /* offline: cards simply show the initial instead of a picture */ }
        }
        foreach (var it in items)
            if (it.NexusId is { } id && _nexusInfo.TryGetValue(id, out var s))
                it.Thumb = await _images.GetAsync(s.ThumbnailUrl);
    }

    Task InstallExploreAsync(ExploreItem item) => OpenNexusForInstallAsync(item.Mod.ModId, item.Name);

    async Task OpenNexusForInstallAsync(int modId, string name)
    {
        if (!GameFound) { Status = Loc.T("Configura la carpeta del juego antes de instalar mods."); return; }
        await OpenNexusPageAsync(modId);
        Status = Loc.F("«{0}»: ", name) + Status;
    }

    // ---------------------------------------------------------------- mod detail

    public ObservableCollection<GalleryImage> DetailImages { get; } = new();

    [ObservableProperty] public partial bool DetailVisible { get; set; }
    [ObservableProperty] public partial bool DetailLoading { get; set; }
    [ObservableProperty] public partial string DetailTitle { get; set; } = "";
    [ObservableProperty] public partial string DetailSubtitle { get; set; } = "";
    [ObservableProperty] public partial string DetailStats { get; set; } = "";
    [ObservableProperty] public partial string DetailTags { get; set; } = "";
    [ObservableProperty] public partial string DetailDescription { get; set; } = "";
    [ObservableProperty] public partial string DetailNote { get; set; } = "";
    [ObservableProperty] public partial int DetailNexusId { get; set; }

    public bool DetailHasNexus => DetailNexusId > 0;
    public bool DetailHasNote => DetailNote.Length > 0;
    partial void OnDetailNoteChanged(string value) => OnPropertyChanged(nameof(DetailHasNote));
    public bool DetailHasImages => DetailImages.Count > 0;
    int _detailToken;

    partial void OnDetailNexusIdChanged(int value) => OnPropertyChanged(nameof(DetailHasNexus));

    void OpenDetail(string title, string subtitle, string description, int nexusId)
    {
        _detailToken++;
        DetailImages.Clear();
        OnPropertyChanged(nameof(DetailHasImages));
        DetailTitle = title; DetailSubtitle = subtitle; DetailDescription = description;
        DetailStats = ""; DetailTags = ""; DetailNote = "";
        DetailNexusId = nexusId;
        DetailVisible = true;
    }

    Task ShowModDetailAsync(ModItem item)
    {
        var m = item.Mod.Manifest;
        _detailMod = item;
        OnPropertyChanged(nameof(DetailHasConfig));
        OpenDetail(item.Name, $"{item.Author} · v{item.Version} · {item.StateText}", m.Description, item.NexusId ?? 0);
        if (item.NexusId is not { } id) { DetailNote = Loc.T("Este mod no indica su página de Nexus en el manifest; no hay más información disponible."); return Task.CompletedTask; }
        return LoadDetailAsync(id, _detailToken);
    }

    Task ShowExploreDetailAsync(ExploreItem item)
    {
        var m = item.Mod;
        _detailMod = null;
        OnPropertyChanged(nameof(DetailHasConfig));
        OpenDetail(m.Name, $"{m.Author} · v{m.Version}", m.Summary, m.ModId);
        DetailStats = item.Stats;
        return LoadDetailAsync(m.ModId, _detailToken);
    }

    async Task LoadDetailAsync(int modId, int token)
    {
        DetailLoading = true;
        try
        {
            var d = await _catalog.GetDetailAsync(modId);
            if (d is null || token != _detailToken) return;
            var s = d.Summary;
            DetailSubtitle = $"{s.Author} · v{s.Version}" + (d.Category is { Length: > 0 } c ? $" · {c}" : "");
            DetailStats = $"♥ {ExploreItem.Compact(s.Endorsements)}   ⬇ {ExploreItem.Compact(s.Downloads)}" + (s.UpdatedAt is { } u ? Loc.F("   actualizado {0:d}", u) : "");
            DetailTags = string.Join(" · ", d.Tags);
            DetailDescription = d.Description.Length > 0 ? d.Description : s.Summary;

            var tasks = d.Images.Select(url => _images.GetAsync(url, 640)).ToList();
            foreach (var t in tasks)
            {
                var bmp = await t;
                if (token != _detailToken) return;
                if (bmp is null) continue;
                DetailImages.Add(new GalleryImage { Image = bmp });
                OnPropertyChanged(nameof(DetailHasImages));
            }
        }
        catch (Exception ex) { if (token == _detailToken) DetailNote = Loc.T("No se pudo cargar la información de Nexus: ") + ex.Message; }
        finally { if (token == _detailToken) DetailLoading = false; }
    }

    [RelayCommand] void CloseDetail() { _detailToken++; DetailVisible = false; DetailImages.Clear(); DetailLoading = false; }
    [RelayCommand] void OpenDetailInNexus() { if (DetailNexusId > 0) Shell.OpenUrl($"https://www.nexusmods.com/stardewvalley/mods/{DetailNexusId}"); }
    [RelayCommand] Task InstallDetailAsync() => DetailNexusId > 0 ? OpenNexusForInstallAsync(DetailNexusId, DetailTitle) : Task.CompletedTask;

    // ---------------------------------------------------------------- SMAPI status

    SmapiRelease? _smapiLatest;
    [ObservableProperty] public partial bool SmapiBannerVisible { get; set; }
    [ObservableProperty] public partial string SmapiBannerText { get; set; } = "";
    [ObservableProperty] public partial string SmapiInfoText { get; set; } = "";
    [ObservableProperty] public partial string SmapiActionText { get; set; } = Loc.T("Instalar SMAPI");
    [ObservableProperty] public partial bool SmapiNeedsAction { get; set; }

    [RelayCommand] Task CheckSmapiAsync() => RefreshSmapiAsync(queryLatest: true, announce: true);

    async Task RefreshSmapiAsync(bool queryLatest, bool announce = false)
    {
        if (queryLatest && GameFound)
        {
            try { _smapiLatest = await new SmapiInstaller(_http).LatestAsync(); }
            catch { /* offline: show the local status only */ }
        }
        UpdateSmapiTexts();
        if (announce) Status = GameFound ? SmapiInfoText : Loc.T("Configura primero la carpeta del juego.");
    }

    void UpdateSmapiTexts()
    {
        if (!GameFound)
        {
            SmapiBannerVisible = false; SmapiNeedsAction = false;
            SmapiInfoText = Loc.T("Configura primero la carpeta del juego.");
            return;
        }
        var st = SmapiInfo.Check(GamePath, _smapiLatest);
        var latest = st.LatestVersion is { Length: > 0 } l ? l : null;
        if (!st.Installed)
        {
            SmapiInfoText = Loc.T("SMAPI no está instalado.") + (latest is null ? "" : Loc.F(" Última versión disponible: {0}.", latest));
            SmapiActionText = latest is null ? Loc.T("Instalar SMAPI") : Loc.F("Instalar SMAPI {0}", latest);
            SmapiBannerText = Loc.T("SMAPI no está instalado: sin él los mods no cargan. El gestor puede descargar la última versión y abrir su instalador oficial.");
            SmapiNeedsAction = true;
        }
        else if (st.Problem is not null)
        {
            SmapiInfoText = Loc.F("SMAPI {0} instalado, pero: {1}", st.InstalledVersion ?? "?", st.Problem);
            SmapiActionText = Loc.T("Reparar SMAPI");
            SmapiBannerText = SmapiInfoText;
            SmapiNeedsAction = true;
        }
        else if (st.UpdateAvailable)
        {
            SmapiInfoText = Loc.F("SMAPI {0} instalado. Hay una versión nueva: {1}.", st.InstalledVersion, latest);
            SmapiActionText = Loc.F("Actualizar a {0}", latest);
            SmapiBannerText = SmapiInfoText;
            SmapiNeedsAction = true;
        }
        else
        {
            SmapiInfoText = Loc.F("SMAPI {0} instalado", st.InstalledVersion ?? Loc.T("(versión desconocida)")) + (latest is null ? "." : Loc.T(" ✔ — es la última versión."));
            SmapiActionText = Loc.T("Reinstalar SMAPI");
            SmapiNeedsAction = false;
        }
        if (SdvMods.Core.Edition.IsNexus && SmapiNeedsAction)
        {
            SmapiActionText = Loc.T("Abrir la web de SMAPI");
            if (!st.Installed) SmapiBannerText = Loc.T("SMAPI no está instalado: sin él los mods no cargan. Descárgalo e instálalo desde su web oficial y vuelve aquí.");
        }
        SmapiBannerVisible = SmapiNeedsAction;
    }
}
