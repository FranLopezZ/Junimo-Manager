using System.Web;

namespace SdvMods.Core.Updates;

/// <summary>nxm://stardewvalley/mods/1915/files/12345?key=abc&amp;expires=1700000000&amp;user_id=1</summary>
public sealed record NxmLink(string Game, int ModId, int FileId, string? Key, long? Expires)
{
    public static bool IsNxm(string? arg) => arg?.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) == true;

    public static NxmLink? Parse(string? raw)
    {
        if (!IsNxm(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var seg = uri.AbsolutePath.Trim('/').Split('/');
        if (seg.Length < 4 || seg[0] != "mods" || seg[2] != "files") return null;
        if (!int.TryParse(seg[1], out var mod) || !int.TryParse(seg[3], out var file)) return null;
        var q = HttpUtility.ParseQueryString(uri.Query);
        long? exp = long.TryParse(q["expires"], out var e) ? e : null;
        return new NxmLink(uri.Host, mod, file, q["key"], exp);
    }
}
