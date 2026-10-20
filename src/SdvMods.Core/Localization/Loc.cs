using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SdvMods.Core.Settings;

namespace SdvMods.Core.Localization;

/// <summary>
/// Minimal localisation. The Spanish text in the code IS the key; every other language is a JSON file
/// ("Spanish text" -> "translation") with an optional "_name" entry (the language's own name).
/// Files are embedded from the repo's /translations folder, and any file dropped in the user's languages folder
/// (<see cref="UserDir"/>) is picked up too, which lets translators test without compiling.
/// A missing entry falls back to English, then to Spanish; it never fails.
/// </summary>
public static class Loc
{
    static Dictionary<string, string> _map = new();
    static Dictionary<string, string> _english = new();

    public static string Language { get; private set; } = "es";

    /// <summary>Folder where users can drop their own translation files (e.g. fr.json).</summary>
    public static string UserDir => Path.Combine(AppPaths.ConfigDir, "languages");

    public static readonly Dictionary<string, string> KnownNames = new()
    {
        ["es"] = "Español", ["en"] = "English", ["pt"] = "Português", ["fr"] = "Français", ["de"] = "Deutsch",
    };

    sealed record Source(string Code, Func<Stream> Open);

    static IEnumerable<Source> Sources()
    {
        var asm = typeof(Loc).Assembly;
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.Contains(".Localization.") || !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var code = res.Split('.')[^2];
            yield return new Source(code, () => asm.GetManifestResourceStream(res)!);
        }
        if (Directory.Exists(UserDir))
            foreach (var f in Directory.EnumerateFiles(UserDir, "*.json"))
            {
                var path = f;
                yield return new Source(Path.GetFileNameWithoutExtension(f), () => File.OpenRead(path));
            }
    }

    static Dictionary<string, string> Read(Source s)
    {
        try
        {
            using var stream = s.Open();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Spanish plus every language file found (a file in the user folder wins over the built-in one).</summary>
    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)> { ("es", KnownNames["es"]) };
        foreach (var s in Sources().Reverse().DistinctBy(s => s.Code, StringComparer.OrdinalIgnoreCase).Reverse())
        {
            if (s.Code == "es") continue;
            var table = Read(s);
            var name = table.TryGetValue("_name", out var n) && !string.IsNullOrWhiteSpace(n) ? n
                : KnownNames.TryGetValue(s.Code, out var k) ? k : s.Code;
            list.Add((s.Code, name));
        }
        return list.OrderBy(l => l.Item1 == "es" ? "" : l.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <param name="setting">"auto" (follow the system), or a language code.</param>
    public static void Init(string? setting)
    {
        var codes = Available().Select(a => a.Code).ToList();
        var code = string.IsNullOrWhiteSpace(setting) || setting == "auto" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) code = codes.Contains("en") ? "en" : "es"; // unknown language: English beats Spanish
        Language = code;
        _english = code == "es" ? new() : TableFor("en").ToDictionary(k => k.Key, k => k.Value);
        _map = code == "es" ? new() : TableFor(code).ToDictionary(k => k.Key, k => k.Value);
    }

    /// <summary>The translation table of a language, without switching the active one (used by tests and tools).</summary>
    public static IReadOnlyDictionary<string, string> TableFor(string code)
    {
        var src = Sources().Where(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).LastOrDefault(); // user folder last = wins
        if (src is null) return new Dictionary<string, string>();
        return Read(src).Where(kv => !kv.Key.StartsWith('_') && !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Translate a Spanish source text.</summary>
    public static string T(string es)
        => _map.TryGetValue(es, out var v) ? v : _english.TryGetValue(es, out var en) ? en : es;

    /// <summary>Translate a Spanish format string ("{0}", "{1}"…) and fill it in.</summary>
    public static string F(string es, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(es), args);

    public static bool HasTranslation(string es) => _map.ContainsKey(es);

    /// <summary>Writes a ready-to-translate file: every text with its English version, to be replaced by the translation.</summary>
    public static void WriteTemplate(string path, string languageName = "")
    {
        var template = new Dictionary<string, string> { ["_name"] = languageName };
        foreach (var kv in TableFor("en")) template[kv.Key] = kv.Value;
        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
