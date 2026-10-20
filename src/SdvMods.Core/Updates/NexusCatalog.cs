using SdvMods.Core.Localization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdvMods.Core.Updates;

public sealed record NexusModSummary(
    int ModId, string Name, string Summary, string Author, string? Version,
    string? ThumbnailUrl, string? PictureUrl, long Endorsements, long Downloads, DateTimeOffset? UpdatedAt)
{
    public string PageUrl => $"https://www.nexusmods.com/stardewvalley/mods/{ModId}";
}

public sealed record NexusModDetail(NexusModSummary Summary, string? Category, IReadOnlyList<string> Tags, string Description, IReadOnlyList<string> Images);

public enum CatalogSort { Relevance, Downloads, Endorsements, Updated, Created }

public sealed record CatalogPage(IReadOnlyList<NexusModSummary> Mods, int Total);

/// <summary>
/// Browse / search / mod details through Nexus Mods' public GraphQL endpoint. Reading public mod data needs no account or
/// API key (installing still goes through the user's own free key + nxm:// link). Adult content is always filtered out.
/// </summary>
public sealed partial class NexusCatalog
{
    const string Endpoint = "https://api.nexusmods.com/v2/graphql";
    const string Fields = "modId name summary author version thumbnailUrl pictureUrl endorsements downloads updatedAt";
    readonly HttpClient _http;

    public NexusCatalog(HttpClient http) => _http = http;

    async Task<JsonElement> QueryAsync(string query, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(Endpoint, new { query }, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0 && !doc.RootElement.TryGetProperty("data", out _))
            throw new InvalidOperationException("Nexus: " + errs[0].GetProperty("message").GetString());
        return doc.RootElement.GetProperty("data").Clone();
    }

    static string Lit(string s) => JsonSerializer.Serialize(s);

    public async Task<CatalogPage> SearchAsync(string? text, CatalogSort sort, string? category = null, int offset = 0, int count = 24, CancellationToken ct = default)
    {
        var filter = "gameDomainName:[{value:\"stardewvalley\"}], adultContent:[{value:false}]";
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (hasText) filter += $", nameStemmed:[{{value:{Lit(text!.Trim())}}}]";
        if (!string.IsNullOrWhiteSpace(category)) filter += $", categoryName:[{{value:{Lit(category)}}}]";
        var field = sort switch
        {
            CatalogSort.Downloads => "downloads",
            CatalogSort.Endorsements => "endorsements",
            CatalogSort.Updated => "updatedAt",
            CatalogSort.Created => "createdAt",
            _ => hasText ? "relevance" : "endorsements",
        };
        var q = $"{{ mods(filter:{{{filter}}}, count:{count}, offset:{offset}, sort:[{{{field}:{{direction:DESC}}}}]) {{ totalCount nodes {{ {Fields} }} }} }}";
        var data = await QueryAsync(q, ct);
        var mods = data.GetProperty("mods");
        return new CatalogPage(mods.GetProperty("nodes").EnumerateArray().Select(Parse).ToList(), mods.GetProperty("totalCount").GetInt32());
    }

    /// <summary>Basic info (name, picture, ...) for many mods at once, keyed by Nexus mod id.</summary>
    public async Task<Dictionary<int, NexusModSummary>> GetManyAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<int, NexusModSummary>();
        foreach (var chunk in ids.Distinct().Chunk(40))
        {
            var list = string.Join(",", chunk.Select(i => $"{{gameDomain:\"stardewvalley\", modId:{i}}}"));
            var data = await QueryAsync($"{{ legacyModsByDomain(ids:[{list}]) {{ nodes {{ {Fields} }} }} }}", ct);
            foreach (var n in data.GetProperty("legacyModsByDomain").GetProperty("nodes").EnumerateArray())
            {
                var s = Parse(n);
                result[s.ModId] = s;
            }
        }
        return result;
    }

    public async Task<NexusModDetail?> GetDetailAsync(int modId, CancellationToken ct = default)
    {
        var data = await QueryAsync($"{{ mod(modId:\"{modId}\", gameId:\"1303\") {{ {Fields} description tags {{ name }} modCategory {{ name }} }} }}", ct);
        if (data.GetProperty("mod").ValueKind != JsonValueKind.Object) return null;
        var m = data.GetProperty("mod");
        var desc = m.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        var tags = m.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        var cat = m.TryGetProperty("modCategory", out var c) && c.ValueKind == JsonValueKind.Object ? c.GetProperty("name").GetString() : null;
        var summary = Parse(m);

        var images = new List<string>();
        if (summary.PictureUrl is not null) images.Add(summary.PictureUrl);
        foreach (var u in ImageUrls(desc)) if (!images.Contains(u)) images.Add(u);
        return new NexusModDetail(summary, cat, tags, ToPlainText(desc), images.Take(12).ToList());
    }

    static NexusModSummary Parse(JsonElement n)
    {
        string? S(string p) => n.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long L(string p) => n.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        DateTimeOffset? upd = DateTimeOffset.TryParse(S("updatedAt"), out var dt) ? dt : null;
        return new NexusModSummary(n.GetProperty("modId").GetInt32(), WebUtility.HtmlDecode(S("name") ?? ""), WebUtility.HtmlDecode(S("summary") ?? ""),
            S("author") ?? "", S("version"), S("thumbnailUrl"), S("pictureUrl"), L("endorsements"), L("downloads"), upd);
    }

    [GeneratedRegex("<img[^>]+src=[\"'](?<u>https?://[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgTag();
    [GeneratedRegex("\\[img\\](?<u>https?://[^\\[\\s]+)\\[/img\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ImgBb();
    [GeneratedRegex("<br\\s*/?>|</p>|</li>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();
    [GeneratedRegex("\\[/?[a-z*]+(=[^\\]]*)?\\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbTag();
    [GeneratedRegex("(\\r?\\n[ \\t]*){3,}")]
    private static partial Regex ManyBlank();

    static IEnumerable<string> ImageUrls(string desc)
        => ImgTag().Matches(desc).Select(m => m.Groups["u"].Value)
            .Concat(ImgBb().Matches(desc).Select(m => m.Groups["u"].Value))
            .Select(WebUtility.HtmlDecode).Where(u => u is not null && !u.Contains("emoji", StringComparison.OrdinalIgnoreCase)).Select(u => u!);

    /// <summary>Nexus descriptions are HTML / BBCode; the app shows them as plain text.</summary>
    public static string ToPlainText(string desc)
    {
        var s = LineBreak().Replace(desc, "\n");
        s = AnyTag().Replace(s, "");
        s = BbTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = ManyBlank().Replace(s, "\n\n");
        return s.Trim();
    }
}
