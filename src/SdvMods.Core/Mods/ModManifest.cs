using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdvMods.Core.Mods;

public sealed class ModDependency
{
    public string UniqueID { get; set; } = "";
    public string? MinimumVersion { get; set; }
    public bool IsRequired { get; set; } = true;
}

public sealed class ContentPackFor
{
    public string UniqueID { get; set; } = "";
    public string? MinimumVersion { get; set; }
}

/// <summary>Subset of a SMAPI manifest.json that the manager needs.</summary>
public sealed class ModManifest
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Description { get; set; } = "";
    public string UniqueID { get; set; } = "";
    public string? MinimumApiVersion { get; set; }
    public string? EntryDll { get; set; }
    public ContentPackFor? ContentPackFor { get; set; }
    public List<ModDependency> Dependencies { get; set; } = new();
    public List<string> UpdateKeys { get; set; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static ModManifest? Parse(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ModManifest>(json.TrimStart('﻿'), Options);
            if (m is null || string.IsNullOrWhiteSpace(m.UniqueID)) return null;
            m.Dependencies ??= new();
            m.UpdateKeys ??= new();
            return m;
        }
        catch (JsonException) { return null; }
    }

    public static ModManifest? Load(string manifestPath)
        => File.Exists(manifestPath) ? Parse(File.ReadAllText(manifestPath)) : null;
}

/// <summary>A mod found on disk.</summary>
public sealed record InstalledMod(ModManifest Manifest, string Folder, bool Enabled)
{
    public string UniqueID => Manifest.UniqueID;
    public string Name => Manifest.Name;
    public string Version => Manifest.Version;
}
