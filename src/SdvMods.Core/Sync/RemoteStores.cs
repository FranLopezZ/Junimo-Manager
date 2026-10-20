using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SdvMods.Core.Localization;
using SdvMods.Core.Profiles;

namespace SdvMods.Core.Sync;

/// <summary>The shared place where profiles are kept, one file per profile.</summary>
public interface IRemoteProfileStore
{
    string Name { get; }
    /// <summary>Every profile found there (including deleted ones, which stay as small markers so the deletion reaches other devices).</summary>
    Task<IReadOnlyList<RemoteProfile>> ReadAsync(CancellationToken ct = default);
    /// <summary>Creates or replaces these profiles, leaving the others untouched.</summary>
    Task WriteAsync(IReadOnlyList<RemoteProfile> profiles, CancellationToken ct = default);
}

static class RemoteNames
{
    public const string LegacyFile = "sdvmods-profiles.json";
    public const string Prefix = "junimo-profile-";
    public static string FileFor(string profileId) => Prefix + profileId + ".json";
    public static bool IsProfileFile(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Profiles from the old single-file format, only for ids that have no file of their own yet.</summary>
    public static IEnumerable<RemoteProfile> FromLegacy(string json, ISet<string> already)
    {
        ProfilesDocument doc;
        try { doc = ProfilesDocument.FromJson(json); } catch (JsonException) { yield break; }
        foreach (var p in doc.Profiles)
            if (!already.Contains(p.Id)) yield return RemoteProfile.FromLegacy(p);
    }
}

/// <summary>A folder you already sync with something else (Syncthing, Dropbox, OneDrive, Google Drive...).</summary>
public sealed class FolderProfileStore : IRemoteProfileStore
{
    readonly string _folder;
    public FolderProfileStore(string folder) => _folder = folder;
    public string Name => Loc.T("Carpeta");

    public Task<IReadOnlyList<RemoteProfile>> ReadAsync(CancellationToken ct = default)
    {
        var list = new List<RemoteProfile>();
        if (Directory.Exists(_folder))
        {
            foreach (var f in Directory.EnumerateFiles(_folder, RemoteNames.Prefix + "*.json"))
            {
                try { if (RemoteProfile.FromJson(File.ReadAllText(f)) is { Id.Length: > 0 } p) list.Add(p); }
                catch (JsonException) { /* a half-synced file: the next round gets it */ }
            }
            var legacy = Path.Combine(_folder, RemoteNames.LegacyFile);
            if (File.Exists(legacy))
                list.AddRange(RemoteNames.FromLegacy(File.ReadAllText(legacy), list.Select(p => p.Id).ToHashSet()));
        }
        return Task.FromResult<IReadOnlyList<RemoteProfile>>(list);
    }

    public Task WriteAsync(IReadOnlyList<RemoteProfile> profiles, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_folder);
        foreach (var p in profiles)
        {
            var file = Path.Combine(_folder, RemoteNames.FileFor(p.Id));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, p.ToJson());
            File.Move(tmp, file, overwrite: true);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the profiles in a private GitHub Gist of the user's own account (one file per profile), so every user has their own
/// copy without any server run by the tool's author. Needs a token with the "gist" scope.
/// </summary>
public sealed class GistProfileStore : IRemoteProfileStore
{
    const string Description = "Junimo Manager profiles";
    readonly HttpClient _http;
    readonly string _token;

    public string? GistId { get; private set; }
    public string Name => "GitHub Gist";

    public GistProfileStore(HttpClient http, string token, string? gistId)
    {
        _http = http; _token = token; GistId = string.IsNullOrWhiteSpace(gistId) ? null : gistId;
    }

    HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        r.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    async Task<string?> FindGistAsync(CancellationToken ct)
    {
        for (var page = 1; page <= 5; page++)
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/gists?per_page=100&page={page}"), ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var arr = doc.RootElement.EnumerateArray().ToList();
            foreach (var g in arr)
            {
                var files = g.GetProperty("files");
                var ours = files.TryGetProperty(RemoteNames.LegacyFile, out _) ||
                           files.EnumerateObject().Any(f => RemoteNames.IsProfileFile(f.Name)) ||
                           (g.TryGetProperty("description", out var d) && d.GetString() == Description);
                if (ours) return g.GetProperty("id").GetString();
            }
            if (arr.Count < 100) break;
        }
        return null;
    }

    async Task<string> ContentAsync(JsonElement file, CancellationToken ct)
        => file.TryGetProperty("truncated", out var t) && t.GetBoolean()
            ? await _http.GetStringAsync(file.GetProperty("raw_url").GetString(), ct)
            : file.GetProperty("content").GetString() ?? "";

    public async Task<IReadOnlyList<RemoteProfile>> ReadAsync(CancellationToken ct = default)
    {
        GistId ??= await FindGistAsync(ct);
        if (GistId is null) return Array.Empty<RemoteProfile>();
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/gists/{GistId}"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { GistId = null; return Array.Empty<RemoteProfile>(); }
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var list = new List<RemoteProfile>();
        string? legacyJson = null;
        foreach (var f in doc.RootElement.GetProperty("files").EnumerateObject())
        {
            if (RemoteNames.IsProfileFile(f.Name))
            {
                try { if (RemoteProfile.FromJson(await ContentAsync(f.Value, ct)) is { Id.Length: > 0 } p) list.Add(p); }
                catch (JsonException) { }
            }
            else if (f.Name == RemoteNames.LegacyFile) legacyJson = await ContentAsync(f.Value, ct);
        }
        if (legacyJson is not null) list.AddRange(RemoteNames.FromLegacy(legacyJson, list.Select(p => p.Id).ToHashSet()));
        return list;
    }

    public async Task WriteAsync(IReadOnlyList<RemoteProfile> profiles, CancellationToken ct = default)
    {
        if (profiles.Count == 0) return;
        GistId ??= await FindGistAsync(ct);
        var files = profiles.ToDictionary(p => RemoteNames.FileFor(p.Id), p => (object)new { content = p.ToJson() });
        if (GistId is null)
        {
            using var create = await _http.SendAsync(Req(HttpMethod.Post, "https://api.github.com/gists",
                new { description = Description, @public = false, files }), ct);
            create.EnsureSuccessStatusCode();
            using var d = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            GistId = d.RootElement.GetProperty("id").GetString();
            return;
        }
        using var resp = await _http.SendAsync(Req(HttpMethod.Patch, $"https://api.github.com/gists/{GistId}", new { files }), ct);
        resp.EnsureSuccessStatusCode();
    }
}
