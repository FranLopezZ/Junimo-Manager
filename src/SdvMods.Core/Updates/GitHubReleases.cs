using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdvMods.Core.Updates;

public sealed record GitHubAsset(string Version, string FileName, string DownloadUrl);

/// <summary>Finds the downloadable archive for a specific mod in a GitHub repo's releases. Fully automatic, no login needed.</summary>
public sealed partial class GitHubReleases
{
    readonly HttpClient _http;
    readonly string? _token;

    public GitHubReleases(HttpClient http, string? token = null) { _http = http; _token = token; }

    async Task<HttpResponseMessage> GetReleasesAsync(string repo, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=40");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new("Bearer", token);
        return await _http.SendAsync(req, ct);
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+(?:-[0-9A-Za-z.]+)?")]
    private static partial Regex VersionPattern();

    static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string? ExtractVersion(string text)
        => VersionPattern().Matches(text).Select(m => m.Value.TrimEnd('.')).LastOrDefault();

    /// <summary>
    /// Repos often hold several mods (monorepos), so "latest release" is not necessarily this mod. Look through the recent
    /// releases for one whose version equals <paramref name="targetVersion"/> (or is newer and clearly named after the mod) and
    /// pick the asset that belongs to this mod.
    /// </summary>
    public async Task<GitHubAsset?> FindAsync(string repo, string targetVersion, string modName, string uniqueId, CancellationToken ct = default)
    {
        // The releases of a public repository need no login; the token only raises the rate limit. If GitHub rejects it
        // (expired or revoked), ask again without it instead of treating the mod as "not downloadable".
        var resp = await GetReleasesAsync(repo, _token, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_token))
        {
            resp.Dispose();
            resp = await GetReleasesAsync(repo, null, ct);
        }
        using var _ = resp;
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var nameKeys = new[] { Norm(modName), Norm(uniqueId.Split('.').Last()) }.Where(k => k.Length >= 3).Distinct().ToArray();
        var target = Mods.SemVer.Parse(targetVersion);
        if (target is null) return null;

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean()) continue;
            if (rel.GetProperty("prerelease").GetBoolean() && target.Prerelease.Length == 0) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";

            var assets = rel.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                            && !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase) && a.Url.Length > 0)
                .ToList();
            if (assets.Count == 0) continue;

            bool Matches((string Name, string Url) a) => nameKeys.Any(k => Norm(a.Name).Contains(k));
            bool tagMatches = nameKeys.Any(k => Norm(tag).Contains(k));

            var named = assets.Where(Matches).ToList();
            var pick = named.Count > 0 ? named : (assets.Count == 1 && (tagMatches || !IsMonorepoTag(tag)) ? assets : null);
            if (pick is null) continue;

            // Platform-agnostic assets first.
            var best = pick.OrderBy(a => a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First();

            var version = ExtractVersion(best.Name) ?? ExtractVersion(tag);
            if (!Mods.SemVer.TryParse(version, out var v)) continue;
            var cmp = v.CompareTo(target);
            if (cmp == 0 || (cmp > 0 && (Matches(best) || tagMatches)))
                return new GitHubAsset(v.ToString(), best.Name, best.Url);
        }
        return null;
    }

    /// <summary>Tags like "BetterGameMenu-0.5.10" or "release/project-fluent/2.0.0" name one mod; plain "v1.2.3" tags name the whole repo.</summary>
    static bool IsMonorepoTag(string tag) => Regex.IsMatch(tag, @"[A-Za-z]{3,}");
}
