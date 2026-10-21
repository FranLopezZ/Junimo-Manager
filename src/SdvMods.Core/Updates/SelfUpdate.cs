using System.Reflection;
using System.Text.Json;
using SdvMods.Core.Mods;

namespace SdvMods.Core.Updates;

public sealed record AppRelease(string Version, string PageUrl, string? AssetUrl, string Notes);

/// <summary>This app's own version, and a check for a newer release in the project's GitHub repository.</summary>
public static class AppInfo
{
    public const string Repo = "FranLopezZ/Junimo-Manager";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string ReleasesUrl = RepoUrl + "/releases";

    public static string Version { get; } = ReadVersion();

    static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+', '-')[0];
        var v = typeof(AppInfo).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Latest published release, or null if there is none / the repo is unreachable.</summary>
    public static async Task<AppRelease?> LatestReleaseAsync(HttpClient http, CancellationToken ct = default)
    {
        if (!Edition.SelfUpdate) return null; // the Nexus edition is updated from its Nexus page
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return ParseRelease(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public static AppRelease? ParseRelease(JsonElement root) => ParseRelease(root, OperatingSystem.IsWindows());

    public static AppRelease? ParseRelease(JsonElement root, bool windows)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesUrl : ReleasesUrl;
        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        var wanted = windows ? "win-x64.zip" : "linux-x64.zip";
        string? asset = null, assetName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
                if ((a.GetProperty("name").GetString() ?? "").EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                { asset = a.GetProperty("browser_download_url").GetString(); assetName = a.GetProperty("name").GetString(); }
        // The tag may be a name like "latest": then the version is the one in the file name (JunimoManager-0.3.0-win-x64.zip).
        var version = tag.TrimStart('v', 'V');
        if (!SemVer.TryParse(version, out _) && assetName is not null
            && System.Text.RegularExpressions.Regex.Match(assetName, @"JunimoManager-(\d+(?:\.\d+)+)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m)
            version = m.Groups[1].Value;
        return new AppRelease(version, page, asset, notes);
    }

    public static bool IsNewer(AppRelease release) => SemVer.IsNewer(release.Version, Version);
}
