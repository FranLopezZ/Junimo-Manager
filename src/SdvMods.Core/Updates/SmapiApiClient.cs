using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdvMods.Core.Updates;

public sealed record SmapiModInfo(string Id, string? LatestVersion, string? PageUrl, int? NexusId, string? GitHubRepo, string? Name);

/// <summary>Free update-check service run by the SMAPI project (https://smapi.io). No account needed.</summary>
public sealed class SmapiApiClient
{
    const string Endpoint = "https://smapi.io/api/v3.0/mods";
    readonly HttpClient _http;

    public SmapiApiClient(HttpClient http) => _http = http;

    sealed record Req(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("updateKeys")] string[] UpdateKeys,
        [property: JsonPropertyName("installedVersion")] string InstalledVersion);

    public async Task<Dictionary<string, SmapiModInfo>> CheckAsync(
        IEnumerable<(string Id, IEnumerable<string> UpdateKeys, string Version)> mods,
        string smapiVersion = "4.1.0", string gameVersion = "1.6.15", CancellationToken ct = default)
    {
        var body = new
        {
            mods = mods.Select(m => new Req(m.Id, m.UpdateKeys.ToArray(), m.Version)).ToArray(),
            apiVersion = smapiVersion,
            gameVersion,
            platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Mac" : "Linux",
            includeExtendedMetadata = true,
        };
        using var resp = await _http.PostAsJsonAsync(Endpoint, body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var result = new Dictionary<string, SmapiModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            string? ver = null, url = null, repo = null, name = null;
            int? nexus = null;
            if (e.TryGetProperty("suggestedUpdate", out var su) && su.ValueKind == JsonValueKind.Object)
            {
                ver = su.GetProperty("version").GetString();
                url = su.TryGetProperty("url", out var u) ? u.GetString() : null;
            }
            if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
            {
                name = md.TryGetProperty("name", out var n) ? n.GetString() : null;
                repo = md.TryGetProperty("gitHubRepo", out var g) ? g.GetString() : null;
                nexus = md.TryGetProperty("nexusID", out var nx) && nx.ValueKind == JsonValueKind.Number ? nx.GetInt32() : null;
                if (ver is null && md.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.Object)
                {
                    ver = main.TryGetProperty("version", out var mv) ? mv.GetString() : null;
                    url ??= main.TryGetProperty("url", out var mu) ? mu.GetString() : null;
                }
            }
            result[id] = new SmapiModInfo(id, ver, url, nexus, repo, name);
        }
        return result;
    }
}
