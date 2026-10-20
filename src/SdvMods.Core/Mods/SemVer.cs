using System.Text.RegularExpressions;

namespace SdvMods.Core.Mods;

/// <summary>Lenient semantic version (1.2, 1.2.3, 1.2.3.4, optional -prerelease / +build), as used by SMAPI mods.</summary>
public sealed partial class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public int[] Parts { get; }
    public string[] Prerelease { get; }

    SemVer(int[] parts, string[] pre) { Parts = parts; Prerelease = pre; }

    [GeneratedRegex(@"^v?(?<nums>\d+(?:\.\d+)*)(?:-(?<pre>[0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out SemVer version)
    {
        version = new SemVer(new[] { 0 }, Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        var parts = m.Groups["nums"].Value.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var pre = m.Groups["pre"].Success ? m.Groups["pre"].Value.Split('.') : Array.Empty<string>();
        version = new SemVer(parts, pre);
        return true;
    }

    public static SemVer? Parse(string? text) => TryParse(text, out var v) ? v : null;

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var len = Math.Max(Parts.Length, other.Parts.Length);
        for (var i = 0; i < len; i++)
        {
            var a = i < Parts.Length ? Parts[i] : 0;
            var b = i < other.Parts.Length ? other.Parts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;           // release > prerelease
        if (other.Prerelease.Length == 0) return -1;
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var x = Prerelease[i]; var y = other.Prerelease[i];
            var xn = int.TryParse(x, out var xi); var yn = int.TryParse(y, out var yi);
            int c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x, y);
            if (c != 0) return c;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);
    public override int GetHashCode() => string.Join('.', Parts.Reverse().SkipWhile(p => p == 0).Reverse()).GetHashCode();
    public override string ToString()
        => string.Join('.', Parts) + (Prerelease.Length > 0 ? "-" + string.Join('.', Prerelease) : "");

    public static bool IsNewer(string? candidate, string? installed)
        => TryParse(candidate, out var c) && (!TryParse(installed, out var i) || c.CompareTo(i) > 0);
}
