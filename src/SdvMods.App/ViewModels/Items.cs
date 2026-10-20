using SdvMods.Core.Localization;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SdvMods.Core.Mods;
using SdvMods.Core.Profiles;
using SdvMods.Core.Updates;

namespace SdvMods.App.ViewModels;

public partial class ModItem : ObservableObject
{
    public InstalledMod Mod { get; }
    public string Name => Mod.Name;
    public string Version => Mod.Version;
    public string Author => Mod.Manifest.Author;
    public string Description => Mod.Manifest.Description;
    public bool Enabled => Mod.Enabled;
    /// <summary>There is an active profile (otherwise every installed mod is simply "in use").</summary>
    public bool HasProfile { get; }
    public bool InProfile { get; }
    public string StateText => !HasProfile || InProfile
        ? (Enabled ? Loc.T("Activo") : Loc.T("Desactivado"))
        : (Enabled ? Loc.T("Activo (fuera del perfil)") : Loc.T("Fuera del perfil"));
    public string MembershipText => InProfile ? Loc.T("Quitar") : Loc.T("Añadir");
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string ToggleText => Enabled ? Loc.T("Desactivar") : Loc.T("Activar");
    public bool HasUpdateKeys => Mod.Manifest.UpdateKeys.Count > 0;

    [ObservableProperty] public partial bool ConfirmUninstall { get; set; }
    public string UninstallText => ConfirmUninstall ? Loc.T("¿Seguro?") : Loc.T("Desinstalar");

    public int? NexusId { get; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    [ObservableProperty] public partial Bitmap? Thumb { get; set; }
    partial void OnThumbChanged(Bitmap? value) => OnPropertyChanged(nameof(NoThumb));
    public bool NoThumb => Thumb is null;

    public ICommand ToggleCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand DetailCommand { get; }
    public ICommand MembershipCommand { get; }
    public ICommand ConfigCommand { get; }
    /// <summary>The mod has a config.json that can be edited.</summary>
    public bool HasConfig { get; }

    public ModItem(InstalledMod mod, bool hasProfile, bool inProfile, Func<ModItem, Task> toggle, Func<ModItem, Task> uninstall,
                   Func<ModItem, Task> detail, Func<ModItem, Task> membership, Func<ModItem, Task> config)
    {
        Mod = mod;
        HasConfig = ModConfigDocument.Exists(mod);
        ConfigCommand = new AsyncRelayCommand(() => config(this));
        HasProfile = hasProfile;
        InProfile = !hasProfile || inProfile;
        MembershipCommand = new AsyncRelayCommand(() => membership(this));
        NexusId = UpdateKey.NexusId(mod.Manifest.UpdateKeys);
        DetailCommand = new AsyncRelayCommand(() => detail(this));
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
        UninstallCommand = new AsyncRelayCommand(async () =>
        {
            if (!ConfirmUninstall) { ConfirmUninstall = true; return; }
            await uninstall(this);
        });
    }

    partial void OnConfirmUninstallChanged(bool value) => OnPropertyChanged(nameof(UninstallText));
}

public partial class UpdateItem : ObservableObject
{
    public ModUpdate Update { get; }
    public bool IsNew { get; }
    public string Name => Update.Name;
    public string VersionText => IsNew ? Loc.F("nuevo → {0}", Update.NewVersion) : $"{Update.CurrentVersion} → {Update.NewVersion}";
    public string MethodText => Update.Method switch
    {
        UpdateMethod.Auto => Loc.T("Automática (GitHub)"),
        UpdateMethod.NexusBrowser => Loc.T("Nexus: requiere un clic en la web"),
        _ => Loc.T("Manual: abrir página"),
    };
    public string ActionText => Update.Method switch
    {
        UpdateMethod.Auto => IsNew ? Loc.T("Instalar") : Loc.T("Actualizar"),
        UpdateMethod.NexusBrowser => Loc.T("Abrir en Nexus"),
        _ => Loc.T("Abrir página"),
    };
    [ObservableProperty] public partial bool Working { get; set; }
    public ICommand ActionCommand { get; }

    public UpdateItem(ModUpdate update, bool isNew, Func<UpdateItem, Task> act)
    {
        Update = update; IsNew = isNew;
        ActionCommand = new AsyncRelayCommand(() => act(this));
    }
}

public partial class ProfileItem : ObservableObject
{
    public Profile Profile { get; }
    public string Name => Profile.Name;
    public string Summary => Loc.F("{0} mods · {1} activos", Profile.Mods.Count, Profile.Mods.FindAll(m => m.Enabled).Count);
    [ObservableProperty] public partial bool IsActive { get; set; }
    public string ActiveText => IsActive ? Loc.T("● en uso") : "";

    [ObservableProperty] public partial string SyncText { get; set; } = "";
    public ProfileItem(Profile p, bool active, string syncText = "") { Profile = p; IsActive = active; SyncText = syncText; }
    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(ActiveText));
}

/// <summary>A mod from the Nexus catalog shown in the Explore tab.</summary>
public partial class ExploreItem : ObservableObject
{
    public NexusModSummary Mod { get; }
    public string Name => Mod.Name;
    public string Summary => Mod.Summary;
    public string Author => Loc.F("por {0}", Mod.Author);
    public string Stats => $"♥ {Compact(Mod.Endorsements)}   ⬇ {Compact(Mod.Downloads)}" + (Mod.Version is { Length: > 0 } v ? $"   v{v}" : "");
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    [ObservableProperty] public partial Bitmap? Thumb { get; set; }
    partial void OnThumbChanged(Bitmap? value) => OnPropertyChanged(nameof(NoThumb));
    public bool NoThumb => Thumb is null;
    [ObservableProperty] public partial bool Installed { get; set; }
    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(InstallText));
    public string InstallText => Installed ? Loc.T("✔ Instalado") : Loc.T("Instalar");
    public ICommand DetailCommand { get; }
    public ICommand InstallCommand { get; }

    public ExploreItem(NexusModSummary mod, bool installed, Func<ExploreItem, Task> detail, Func<ExploreItem, Task> install)
    {
        Mod = mod; Installed = installed;
        DetailCommand = new AsyncRelayCommand(() => detail(this));
        InstallCommand = new AsyncRelayCommand(() => install(this));
    }

    public static string Compact(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000d:0.#}M",
        >= 1_000 => $"{n / 1_000d:0.#}k",
        _ => n.ToString(),
    };
}

public sealed class GalleryImage
{
    public Bitmap? Image { get; init; }
}

/// <summary>A line of the config.json editor (wraps the model so the view can bind to it).</summary>
public partial class ConfigFieldItem : ObservableObject
{
    readonly ConfigField _field;
    public ConfigFieldItem(ConfigField field) { _field = field; }

    public string Label => _field.Label;
    public Avalonia.Thickness Indent => new(_field.Level * 24, 0, 0, 0);
    public bool IsHeader => _field.Kind == ConfigKind.Header;
    public bool IsBool => _field.Kind == ConfigKind.Bool;
    public bool IsText => _field.Kind is ConfigKind.Text or ConfigKind.Number;
    public bool IsJson => _field.Kind == ConfigKind.Json;
    public string KindHint => _field.Kind switch
    {
        ConfigKind.Number => Loc.T("número"),
        ConfigKind.Json => Loc.T("lista / JSON"),
        _ => "",
    };

    public bool BoolValue { get => _field.BoolValue; set { _field.BoolValue = value; OnPropertyChanged(); } }
    public string Text { get => _field.Text; set { _field.Text = value; OnPropertyChanged(); } }
    public string Error => _field.Error;
    public bool HasError => _field.Error.Length > 0;
    public void RefreshError() { OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError)); }
}
