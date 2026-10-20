using SdvMods.Core.Localization;
using System.Text.Json;

namespace SdvMods.Core.Updates;

public sealed record NexusFile(int FileId, string Name, string? Version, string Category, long UploadedTimestamp);

/// <summary>
/// Nexus Mods API v1. A free personal API key is enough to look up files; for free (non-Premium) accounts the
/// download link additionally needs the key/expires pair that Nexus puts in the nxm:// link when the user
/// presses "Mod Manager Download" / "Slow download" on the website.
/// </summary>
public sealed class NexusClient
{
    const string Base = "https://api.nexusmods.com/v1/games/stardewvalley";
    readonly HttpClient _http;
    readonly string _apiKey;

    public NexusClient(HttpClient http, string apiKey) { _http = http; _apiKey = apiKey; }

    HttpRequestMessage Get(string url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("apikey", _apiKey);
        r.Headers.Add("Application-Name", Http.AppName);
        r.Headers.Add("Application-Version", Http.AppVersion);
        return r;
    }

    public async Task<string?> ValidateAsync(CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Get("https://api.nexusmods.com/v1/users/validate.json"), ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "?";
    }

    public async Task<List<NexusFile>> GetFilesAsync(int modId, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Get($"{Base}/mods/{modId}/files.json?category=main,update,optional"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("files").EnumerateArray().Select(f => new NexusFile(
            f.GetProperty("file_id").GetInt32(),
            f.GetProperty("name").GetString() ?? "",
            f.TryGetProperty("mod_version", out var v) ? v.GetString() : null,
            f.TryGetProperty("category_name", out var c) ? c.GetString() ?? "" : "",
            f.TryGetProperty("uploaded_timestamp", out var t) ? t.GetInt64() : 0)).ToList();
    }

    /// <summary>The file a user would normally want: newest MAIN upload.</summary>
    public async Task<NexusFile?> GetMainFileAsync(int modId, CancellationToken ct = default)
    {
        var files = await GetFilesAsync(modId, ct);
        return files.Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault()
            ?? files.OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault();
    }

    public async Task<string> GetDownloadUrlAsync(NxmLink link, CancellationToken ct = default)
    {
        var url = $"{Base}/mods/{link.ModId}/files/{link.FileId}/download_link.json";
        if (link.Key is not null && link.Expires is not null) url += $"?key={Uri.EscapeDataString(link.Key)}&expires={link.Expires}";
        using var resp = await _http.SendAsync(Get(url), ct);
        if ((int)resp.StatusCode == 403)
            throw new InvalidOperationException(Loc.T("Nexus rechazó la descarga: las cuentas gratuitas deben pulsar «Descarga lenta/Mod Manager» en la web."));
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? first.GetProperty("URI").GetString()! : throw new InvalidOperationException(Loc.T("Nexus no devolvió ningún enlace de descarga."));
    }

    public static string FilePageUrl(int modId, int? fileId = null)
        => $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files" + (fileId is null ? "" : $"&file_id={fileId}&nmm=1");
}
