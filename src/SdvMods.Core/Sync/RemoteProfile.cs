using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SdvMods.Core.Profiles;

namespace SdvMods.Core.Sync;

/// <summary>One profile as stored in the shared place (a file per profile): its mods, its mod settings and the revision it was saved at.</summary>
public sealed class RemoteProfile
{
    /// <summary>Settings bigger than this are left out of the shared copy (they stay on the device).</summary>
    public const int MaxConfigBytes = 256 * 1024;

    public int Format { get; set; } = 2;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rev { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Which device uploaded this revision (informational).</summary>
    public string Device { get; set; } = "";
    public bool Deleted { get; set; }
    public List<ProfileMod> Mods { get; set; } = new();
    public Dictionary<string, string> Configs { get; set; } = new();

    /// <summary>Read from the old single-file format (no revisions): the date decides the first time.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Legacy { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, ProfilesDocument.Json);
    public static RemoteProfile? FromJson(string json) => JsonSerializer.Deserialize<RemoteProfile>(json, ProfilesDocument.Json);

    public static RemoteProfile From(Profile p, IReadOnlyDictionary<string, string> configs, string device) => new()
    {
        Id = p.Id, Name = p.Name, Rev = p.Rev, UpdatedAt = p.UpdatedAt, Device = device, Deleted = p.Deleted,
        Mods = p.Deleted ? new() : p.Mods.Select(Copy).ToList(),
        Configs = p.Deleted ? new() : configs.Where(c => Encoding.UTF8.GetByteCount(c.Value) <= MaxConfigBytes).ToDictionary(c => c.Key, c => c.Value),
    };

    /// <summary>Converts a profile of the old single-file document.</summary>
    public static RemoteProfile FromLegacy(Profile p) => new()
    {
        Id = p.Id, Name = p.Name, UpdatedAt = p.UpdatedAt, Deleted = p.Deleted, Legacy = true,
        Mods = p.Deleted ? new() : p.Mods.Select(Copy).ToList(),
        Rev = "legacy-" + Hash(p.Name, p.Deleted, p.Mods, new Dictionary<string, string>()),
    };

    static ProfileMod Copy(ProfileMod m) => new() { UniqueID = m.UniqueID, Name = m.Name, Version = m.Version, Enabled = m.Enabled, UpdateKeys = m.UpdateKeys.ToList() };

    /// <summary>Same name, mods (id, version, enabled) and settings → same content, whatever the revision numbers say.</summary>
    public static string Hash(string name, bool deleted, IEnumerable<ProfileMod> mods, IReadOnlyDictionary<string, string> configs)
    {
        var sb = new StringBuilder();
        sb.Append(name).Append('|').Append(deleted ? 'D' : 'A');
        if (!deleted)
        {
            foreach (var m in mods.OrderBy(m => m.UniqueID, StringComparer.OrdinalIgnoreCase))
                sb.Append('\n').Append(m.UniqueID.ToLowerInvariant()).Append('|').Append(m.Version).Append('|').Append(m.Enabled ? '1' : '0');
            foreach (var c in configs.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append("\n#").Append(c.Key.ToLowerInvariant()).Append('|').Append(Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(c.Value))));
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public string ContentHash() => Hash(Name, Deleted, Mods, Configs);
}
