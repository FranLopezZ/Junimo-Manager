using SdvMods.Core.Mods;

namespace SdvMods.Core.Updates;

public enum UpdateMethod
{
    /// <summary>Direct download (GitHub release). No user action.</summary>
    Auto,
    /// <summary>Only on Nexus: the user must press the download button in the browser; the nxm:// link then completes it.</summary>
    NexusBrowser,
    /// <summary>Other site: open the page and install the file manually.</summary>
    Manual,
}

public sealed record ModUpdate(
    string UniqueID, string Name, string? CurrentVersion, string NewVersion, UpdateMethod Method,
    string? DownloadUrl, string? FileName, int? NexusModId, string? PageUrl);

/// <summary>Decides, per mod, whether a newer version exists and how it can be obtained.</summary>
public sealed class UpdateService
{
    readonly SmapiApiClient _smapi;
    readonly GitHubReleases _github;

    public UpdateService(HttpClient http, string? githubToken = null)
    {
        _smapi = new SmapiApiClient(http);
        _github = new GitHubReleases(http, githubToken);
    }

    public Task<List<ModUpdate>> CheckAsync(IEnumerable<InstalledMod> installed, CancellationToken ct = default)
        => ResolveAsync(installed.Where(m => !ModScanner.IsBundled(m))
                .Select(m => (m.UniqueID, m.Name, (string?)m.Version, (IReadOnlyList<string>)m.Manifest.UpdateKeys)), onlyNewer: true, ct);

    /// <summary>Find a download source for mods that are not installed yet (e.g. when applying a synced profile).</summary>
    public Task<List<ModUpdate>> ResolveMissingAsync(IEnumerable<(string Id, string Name, IReadOnlyList<string> Keys)> mods, CancellationToken ct = default)
        => ResolveAsync(mods.Select(m => (m.Id, m.Name, (string?)null, m.Keys)), onlyNewer: false, ct);

    async Task<List<ModUpdate>> ResolveAsync(IEnumerable<(string Id, string Name, string? Version, IReadOnlyList<string> Keys)> mods, bool onlyNewer, CancellationToken ct)
    {
        var list = mods.ToList();
        if (list.Count == 0) return new();
        var info = await _smapi.CheckAsync(list.Select(m => (m.Id, (IEnumerable<string>)m.Keys, m.Version ?? "0.0.1")), ct: ct);

        var result = new List<ModUpdate>();
        foreach (var m in list)
        {
            if (!info.TryGetValue(m.Id, out var i) || i.LatestVersion is null)
            {
                // smapi.io does not know the mod: for a missing mod the profile's own Nexus/GitHub key is still enough to fetch it.
                if (!onlyNewer && UpdateKey.NexusId(m.Keys) is { } nid)
                    result.Add(new(m.Id, m.Name, m.Version, "?", UpdateMethod.NexusBrowser, null, null, nid, NexusClient.FilePageUrl(nid)));
                continue;
            }
            if (onlyNewer && !SemVer.IsNewer(i.LatestVersion, m.Version)) continue;

            var repo = UpdateKey.GitHubRepo(m.Keys) ?? i.GitHubRepo;
            var nexusId = UpdateKey.NexusId(m.Keys) ?? i.NexusId;

            if (repo is not null)
            {
                var asset = await SafeAsync(() => _github.FindAsync(repo, i.LatestVersion, m.Name, m.Id, ct));
                if (asset is not null)
                {
                    result.Add(new(m.Id, m.Name, m.Version, i.LatestVersion, UpdateMethod.Auto, asset.DownloadUrl, asset.FileName, nexusId, i.PageUrl));
                    continue;
                }
            }
            result.Add(new(m.Id, m.Name, m.Version, i.LatestVersion,
                nexusId is not null ? UpdateMethod.NexusBrowser : UpdateMethod.Manual,
                null, null, nexusId, i.PageUrl ?? (nexusId is not null ? NexusClient.FilePageUrl(nexusId.Value) : null)));
        }
        return result;
    }

    static async Task<T?> SafeAsync<T>(Func<Task<T?>> f) where T : class
    {
        try { return await f(); } catch (HttpRequestException) { return null; } catch (TaskCanceledException) { return null; }
    }
}
