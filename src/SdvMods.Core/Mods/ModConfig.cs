using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SdvMods.Core.Localization;

namespace SdvMods.Core.Mods;

public enum ConfigKind { Header, Bool, Number, Text, Json }

/// <summary>One editable line of a mod's config.json.</summary>
public sealed class ConfigField
{
    public string[] Path { get; init; } = Array.Empty<string>();
    public string Label => Path.Length > 0 ? Path[^1] : "";
    /// <summary>Nesting depth, to indent the values of a sub-section.</summary>
    public int Level => Math.Max(Path.Length - 1, 0);
    public ConfigKind Kind { get; init; }
    public bool BoolValue { get; set; }
    /// <summary>Text of a number / string / raw JSON value.</summary>
    public string Text { get; set; } = "";
    public string Error { get; set; } = "";

    internal bool OriginalBool;
    internal string OriginalText = "";
    internal bool Changed => Kind == ConfigKind.Bool ? BoolValue != OriginalBool : Kind != ConfigKind.Header && Text != OriginalText;
}

/// <summary>
/// Generic editor for a SMAPI mod's config.json. Mods have no schema, so the form is built from the JSON itself:
/// booleans become checkboxes, numbers and text become fields, sub-objects become sections and arrays are edited as JSON.
/// Unchanged values keep their exact original form, and the previous file is kept as config.json.bak.
/// </summary>
public sealed class ModConfigDocument
{
    public const string FileName = "config.json";
    static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly JsonObject _root;
    public string FilePath { get; }
    public List<ConfigField> Fields { get; } = new();

    ModConfigDocument(string file, JsonObject root) { FilePath = file; _root = root; }

    public static string ConfigPath(InstalledMod mod) => Path.Combine(mod.Folder, FileName);
    public static bool Exists(InstalledMod mod) => File.Exists(ConfigPath(mod));

    public static ModConfigDocument Load(string file)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(file).TrimStart('﻿'), null, ReadOptions); }
        catch (JsonException) { throw new InvalidDataException(Loc.T("El config.json de este mod no es un JSON válido.")); }
        if (node is not JsonObject root) throw new InvalidDataException(Loc.T("El config.json de este mod no tiene el formato esperado (un objeto JSON)."));
        var doc = new ModConfigDocument(file, root);
        doc.Flatten(root, Array.Empty<string>());
        return doc;
    }

    void Flatten(JsonObject obj, string[] prefix)
    {
        foreach (var (name, value) in obj)
        {
            var path = prefix.Append(name).ToArray();
            switch (value?.GetValueKind())
            {
                case JsonValueKind.Object when value is JsonObject child && child.Count > 0:
                    Fields.Add(new ConfigField { Path = path, Kind = ConfigKind.Header });
                    Flatten(child, path);
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    var b = value!.GetValue<bool>();
                    Fields.Add(new ConfigField { Path = path, Kind = ConfigKind.Bool, BoolValue = b, OriginalBool = b });
                    break;
                case JsonValueKind.Number:
                    Add(path, ConfigKind.Number, value!.ToJsonString());
                    break;
                case JsonValueKind.String:
                    Add(path, ConfigKind.Text, value!.GetValue<string>());
                    break;
                default: // arrays, empty objects, null
                    Add(path, ConfigKind.Json, value?.ToJsonString() ?? "null");
                    break;
            }
        }
    }

    void Add(string[] path, ConfigKind kind, string text)
        => Fields.Add(new ConfigField { Path = path, Kind = kind, Text = text, OriginalText = text });

    /// <summary>Validates and writes the changes. Returns false (with the problems stored in each field's Error) if something is invalid.</summary>
    public bool TrySave()
    {
        var edits = new List<(JsonObject Parent, string Name, JsonNode? Value)>();
        var ok = true;
        foreach (var f in Fields)
        {
            f.Error = "";
            if (!f.Changed) continue;
            JsonObject parent = _root;
            foreach (var step in f.Path[..^1]) parent = parent[step]!.AsObject();
            switch (f.Kind)
            {
                case ConfigKind.Bool:
                    edits.Add((parent, f.Label, JsonValue.Create(f.BoolValue)));
                    break;
                case ConfigKind.Text:
                    edits.Add((parent, f.Label, JsonValue.Create(f.Text)));
                    break;
                case ConfigKind.Number:
                    var t = f.Text.Trim().Replace(',', '.');
                    if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) edits.Add((parent, f.Label, JsonValue.Create(l)));
                    else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) edits.Add((parent, f.Label, JsonValue.Create(d)));
                    else { f.Error = Loc.T("Escribe un número."); ok = false; }
                    break;
                case ConfigKind.Json:
                    try { edits.Add((parent, f.Label, JsonNode.Parse(f.Text, null, ReadOptions))); }
                    catch (JsonException) { f.Error = Loc.T("JSON no válido (p. ej. [\"A\", \"B\"] o {\"x\": 1})."); ok = false; }
                    break;
            }
        }
        if (!ok) return false;
        if (edits.Count == 0) return true;

        foreach (var (parent, name, value) in edits) parent[name] = value;
        var text = _root.ToJsonString(WriteOptions);
        File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, FilePath, overwrite: true);
        foreach (var f in Fields.Where(x => x.Kind != ConfigKind.Header)) { f.OriginalBool = f.BoolValue; f.OriginalText = f.Text; }
        return true;
    }

    /// <summary>Deletes config.json (a copy stays as config.json.bak): the mod writes a fresh one with its defaults on the next game launch.</summary>
    public static void RestoreDefaults(string file)
    {
        File.Copy(file, file + ".bak", overwrite: true);
        File.Delete(file);
    }
}
