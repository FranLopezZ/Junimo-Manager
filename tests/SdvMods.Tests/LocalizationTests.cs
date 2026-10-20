using System.Text.RegularExpressions;
using SdvMods.App.Input;
using SdvMods.Core.Localization;

namespace SdvMods.Tests;

public class LocalizationTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JunimoManager.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static string Unescape(string s) => Regex.Replace(s, """\\(.)""", x => x.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

    static HashSet<string> SourceKeys()
    {
        var keys = new HashSet<string>();
        var call = new Regex("""Loc\.[TF]\(\s*\$?"((?:[^"\\]|\\.)*)" """.TrimEnd());
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var n = f.Replace('\\', '/');
            if (n.Contains("/obj/") || n.Contains("/bin/") || n.EndsWith("Loc.cs")) continue;
            foreach (Match m in call.Matches(File.ReadAllText(f))) keys.Add(Unescape(m.Groups[1].Value));
        }
        var attr = new Regex("""\b(?:Text|Content|Header|PlaceholderText)="([^"]*)" """.TrimEnd());
        foreach (var f in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            if (f.Replace('\\', '/').Contains("/obj/")) continue;
            foreach (Match m in attr.Matches(File.ReadAllText(f)))
            {
                var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (v.Length > 0 && !v.StartsWith('{') && Regex.IsMatch(v, "[A-Za-zÁÉÍÓÚáéíóúñ]{2}")) keys.Add(v);
            }
        }
        return keys;
    }

    [Fact]
    public void EveryTextInTheCodeHasAnEnglishTranslation()
    {
        var en = Loc.TableFor("en");
        var missing = SourceKeys().Where(k => !en.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        Assert.True(missing.Count == 0, "Missing in en.json:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TranslationsKeepTheSamePlaceholders()
    {
        static string[] Holes(string s) => Regex.Matches(s, """\{\d+[^}]*\}""").Select(m => m.Value).OrderBy(x => x).ToArray();
        var bad = Loc.TableFor("en").Where(kv => !Holes(kv.Key).SequenceEqual(Holes(kv.Value))).Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, "Placeholder mismatch:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void EveryTranslationFileInTheRepoIsValid()
    {
        static Dictionary<string, string> Load(string f) =>
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!;
        static string[] Holes(string s) => Regex.Matches(s, """\{\d+[^}]*\}""").Select(m => m.Value).OrderBy(x => x).ToArray();

        var folder = Path.Combine(RepoRoot(), "translations");
        var en = Load(Path.Combine(folder, "en.json")).Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value);
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            var table = Load(file);
            if (!table.TryGetValue("_name", out var name) || string.IsNullOrWhiteSpace(name)) problems.Add($"{code}: missing \"_name\"");
            foreach (var (key, value) in table.Where(kv => !kv.Key.StartsWith('_')))
            {
                if (!en.ContainsKey(key)) problems.Add($"{code}: unknown key \"{key}\"");
                else if (value.Length > 0 && !Holes(key).SequenceEqual(Holes(value))) problems.Add($"{code}: placeholders differ in \"{key}\"");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TemplateContainsEveryEnglishTextAndIsLoadable()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "xx.json");
        Loc.WriteTemplate(file, "Test language");
        var table = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Assert.Equal("Test language", table["_name"]);
        Assert.Equal(Loc.TableFor("en").Count + 1, table.Count);
    }

    [Fact]
    public void LanguageListHasSpanishAndEnglish()
    {
        var codes = Loc.Available().Select(a => a.Code).ToList();
        Assert.Contains("es", codes);
        Assert.Contains("en", codes);
    }

    [Fact]
    public void PadMapperFiresOncePerPressAndRepeatsDirections()
    {
        var m = new PadMapper();
        var t = TimeSpan.Zero;
        Assert.Equal(new[] { PadAction.Down, PadAction.Accept }, m.Update(PadButtons.Down | PadButtons.A, t));
        Assert.Empty(m.Update(PadButtons.Down | PadButtons.A, t + TimeSpan.FromMilliseconds(100)));      // held: no repeat yet
        Assert.Equal(new[] { PadAction.Down }, m.Update(PadButtons.Down | PadButtons.A, t + PadMapper.RepeatDelay + TimeSpan.FromMilliseconds(5)));
        Assert.Empty(m.Update(PadButtons.None, t + TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { PadAction.NextTab }, m.Update(PadButtons.RB, t + TimeSpan.FromSeconds(3)));
        Assert.Empty(m.Update(PadButtons.RB, t + TimeSpan.FromSeconds(4)));                              // buttons never repeat
    }
}
