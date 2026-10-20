using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdvMods.Core.Profiles;

public sealed class ProfileMod
{
    public string UniqueID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> UpdateKeys { get; set; } = new();
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Perfil";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Tombstone, so a deletion on one device propagates to the others instead of the profile coming back.</summary>
    public bool Deleted { get; set; }
    public List<ProfileMod> Mods { get; set; } = new();

    /// <summary>Changes on every local modification. Sync compares it with <see cref="BaseRev"/> to tell what changed where.</summary>
    public string Rev { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The revision this device last agreed on with the shared copy (null = never synced).</summary>
    public string? BaseRev { get; set; }
    /// <summary>Per-profile switch: a profile that is not synced stays only on this device.</summary>
    public bool SyncEnabled { get; set; } = true;
}

public sealed class ProfilesDocument
{
    public int Format { get; set; } = 2;
    public List<Profile> Profiles { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ProfilesDocument FromJson(string json) => JsonSerializer.Deserialize<ProfilesDocument>(json, Json) ?? new();
}
