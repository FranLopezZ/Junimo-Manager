namespace SdvMods.Core.Updates;

/// <summary>An entry of a manifest's UpdateKeys, e.g. "Nexus:1915", "Nexus:1915@main", "GitHub:Pathoschild/StardewMods".</summary>
public sealed record UpdateKey(string Site, string Id)
{
    public static UpdateKey? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var i = raw.IndexOf(':');
        if (i <= 0) return null;
        var site = raw[..i].Trim();
        var id = raw[(i + 1)..].Trim();
        var at = id.IndexOf('@');
        if (at >= 0) id = id[..at];
        return id.Length == 0 ? null : new UpdateKey(site, id);
    }

    public static int? NexusId(IEnumerable<string> keys)
        => keys.Select(Parse).FirstOrDefault(k => k is not null && k.Site.Equals("Nexus", StringComparison.OrdinalIgnoreCase)) is { } k
           && int.TryParse(k.Id, out var n) ? n : null;

    public static string? GitHubRepo(IEnumerable<string> keys)
        => keys.Select(Parse).FirstOrDefault(k => k is not null && k.Site.Equals("GitHub", StringComparison.OrdinalIgnoreCase))?.Id;
}
