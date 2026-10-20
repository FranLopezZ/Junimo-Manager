using SdvMods.Core.Localization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SdvMods.Core.Profiles;

/// <summary>Export / import of a single profile as a file or a short text code, so friends can swap mod lists.</summary>
public static class ProfileShare
{
    public const string Prefix = "JUNIMO1:";

    sealed class Shared
    {
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public List<ProfileMod> Mods { get; set; } = new();
        /// <summary>Optional (files only, not codes): the mods' config.json texts by mod id.</summary>
        public Dictionary<string, string>? Configs { get; set; }
    }

    /// <summary>Only names, versions, enabled state and update keys travel; nothing about the device or the account.</summary>
    public static string ToJson(Profile p, IReadOnlyDictionary<string, string>? configs = null)
        => JsonSerializer.Serialize(new Shared
        {
            Format = configs is { Count: > 0 } ? 2 : 1, Name = p.Name, Mods = p.Mods,
            Configs = configs is { Count: > 0 } ? new Dictionary<string, string>(configs) : null,
        }, ProfilesDocument.Json);

    public static string ToCode(Profile p)
    {
        var raw = Encoding.UTF8.GetBytes(ToJson(p));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(raw);
        return Prefix + Convert.ToBase64String(ms.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Accepts a share code or the JSON file content. Returns a brand-new profile (new id), never touching existing ones.</summary>
    public static Profile Import(string text) => ImportWithConfigs(text).Profile;

    /// <summary>Like <see cref="Import"/>, also returning the mod settings the file carried (empty for codes and older files).</summary>
    public static (Profile Profile, Dictionary<string, string> Configs) ImportWithConfigs(string text)
    {
        text = text.Trim().Trim('﻿');
        string json;
        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var b64 = text[Prefix.Length..].Trim().Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            try
            {
                using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                json = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException)
            {
                throw new InvalidDataException(Loc.T("El código está incompleto o dañado."));
            }
        }
        else json = text;

        Shared? s;
        try { s = JsonSerializer.Deserialize<Shared>(json, ProfilesDocument.Json); }
        catch (JsonException) { throw new InvalidDataException(Loc.T("Eso no es un perfil de Junimo Manager.")); }
        if (s is null || s.Mods.Count == 0 && string.IsNullOrWhiteSpace(s.Name)) throw new InvalidDataException(Loc.T("Eso no es un perfil de Junimo Manager."));

        var mods = s.Mods.Where(m => !string.IsNullOrWhiteSpace(m.UniqueID)).ToList();
        foreach (var m in mods) m.UpdateKeys ??= new();
        var configs = (s.Configs ?? new()).Where(kv => mods.Any(m => string.Equals(m.UniqueID, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return (new Profile { Name = string.IsNullOrWhiteSpace(s.Name) ? Loc.T("Perfil importado") : s.Name.Trim(), Mods = mods }, configs);
    }
}
