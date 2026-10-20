using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdvMods.Core.Settings;

public enum SyncMode { None, Gist, Folder }

public static class Branding
{
    /// <summary>
    /// Public Client ID of the GitHub OAuth App used for "Sign in with GitHub" (device flow enabled, no secret needed).
    /// Register one at https://github.com/settings/applications/new and paste its Client ID here before publishing.
    /// Can also be provided with the JUNIMO_GITHUB_CLIENT_ID environment variable.
    /// </summary>
    public const string GitHubClientId = "Ov23liRHOfWCkSc1jkpI";

    public static string EffectiveGitHubClientId
        => Environment.GetEnvironmentVariable("JUNIMO_GITHUB_CLIENT_ID") is { Length: > 0 } e ? e : GitHubClientId;
}

public static class AppPaths
{
    public static string ConfigDir
    {
        get
        {
            // Portable / test mode: JUNIMO_CONFIG_DIR points the app at another settings folder.
            if (Environment.GetEnvironmentVariable("JUNIMO_CONFIG_DIR") is { Length: > 0 } custom) return custom;
            var baseDir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "JunimoManager");
        }
    }

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public static string ProfilesFile => Path.Combine(ConfigDir, "profiles.json");
    public static string ProfileConfigsDir => Path.Combine(ConfigDir, "profile-configs");
    public static string CacheDir => Path.Combine(ConfigDir, "downloads");

    /// <summary>Backups live next to the game so moving them never crosses disks (the Deck's SD card!).</summary>
    public static string BackupsDir(string gamePath) => Path.Combine(gamePath, "JunimoManager-Backups");
}

public sealed class AppSettings
{
    public string? GamePath { get; set; }
    public SyncMode SyncMode { get; set; } = SyncMode.None;
    public string? GistId { get; set; }
    public string? SyncFolder { get; set; }
    public string? ActiveProfileId { get; set; }
    public bool SyncOnStartup { get; set; } = true;
    /// <summary>Each profile keeps its own copy of the mods' config.json files.</summary>
    public bool PerProfileConfigs { get; set; } = true;
    /// <summary>Upload local changes by themselves a few seconds after they happen (otherwise only the Sync / Upload buttons do).</summary>
    public bool SyncAuto { get; set; } = true;
    /// <summary>"auto" follows the system language.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"auto": big-button controller mode only when launched from Steam Gaming Mode.</summary>
    public string ControllerMode { get; set; } = "auto";

    // Secrets are kept in memory as plain strings but written to disk protected (see Secrets).
    [JsonIgnore] public string? NexusApiKey { get; set; }
    [JsonIgnore] public string? GitHubToken { get; set; }
    [JsonIgnore] public string? GitHubRefreshToken { get; set; }
    /// <summary>When the GitHub access token stops working (only for OAuth apps that issue expiring tokens).</summary>
    public DateTimeOffset? GitHubTokenExpiresAt { get; set; }

    [JsonPropertyName("NexusApiKeyProtected")]
    public string? NexusApiKeyProtected { get => Secrets.Protect(NexusApiKey); set => NexusApiKey = Secrets.Unprotect(value); }

    [JsonPropertyName("GitHubRefreshTokenProtected")]
    public string? GitHubRefreshTokenProtected { get => Secrets.Protect(GitHubRefreshToken); set => GitHubRefreshToken = Secrets.Unprotect(value); }

    [JsonPropertyName("GitHubTokenProtected")]
    public string? GitHubTokenProtected { get => Secrets.Protect(GitHubToken); set => GitHubToken = Secrets.Unprotect(value); }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(AppPaths.SettingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}
