using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;

namespace Pylon;

/// <summary>
/// UI language. Texts are written in English in code and looked up in a dictionary keyed by that English text: Russian
/// is built in (<see cref="Strings.Ru"/>), any other language is a JSON file in "lang" next to Pylon.exe or in the data
/// folder ({ "_name": "Deutsch", "Open File(s)": "Datei(en) öffnen", … }; the file name is the language code, de.json).
/// "Pylon.exe --export-strings file.json" writes every English text as a template. Missing keys fall back to English.
/// Applied at startup (Settings → General → Interface language).
/// </summary>
public static class L
{
    public static bool Ru => Code == "ru";

    /// <summary>Two-letter code of the active language ("en" for English).</summary>
    public static string Code { get; private set; } = "en";

    static Dictionary<string, string> _table = [];

    /// <summary>Language files found: code → (native name, path).</summary>
    public static Dictionary<string, (string Name, string Path)> Files { get; } = Scan();

    /// <summary>What Settings offers: System, English, Русский and every language file by its own name.</summary>
    public static string[] Choices => ["System", "English", "Русский", .. Files.Values.Select(f => f.Name).Order()];

    public static void Init()
    {
        var chosen = Settings.Str("general.language");
        Code = chosen switch
        {
            "English" => "en",
            "Русский" => "ru",
            "System" or "" => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            _ => Files.FirstOrDefault(f => f.Value.Name == chosen).Key ?? "en",
        };
        _table = Code == "ru" ? Strings.Ru : Files.TryGetValue(Code, out var f) ? Load(f.Path) : [];
        if (_table.Count == 0 && Code != "ru") Code = "en";
    }

    static Dictionary<string, (string, string)> Scan()
    {
        var found = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "lang"), Path.Combine(Library.Dir, "lang") }) // the user's own files win
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "??.json").Concat(Directory.EnumerateFiles(dir, "??-??.json")))
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    if (code is "en" or "ru") continue;
                    found[code] = (Load(file).GetValueOrDefault("_name") ?? code, file);
                }
        return found;
    }

    static Dictionary<string, string> Load(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } d ? d.Where(p => p.Value.Length > 0).ToDictionary() : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Every English text Pylon translates, as a template for a new language (empty values = not translated yet).</summary>
    public static void ExportTemplate(string path)
    {
        var template = new Dictionary<string, string> { ["_name"] = "" };
        foreach (var key in Strings.Ru.Keys.Append("{0} item").Append("{0} items").Append("{0} items (few)")) template[key] = "";
        File.WriteAllText(path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    /// <summary>Translation of an exact English UI string; unknown strings (file names, tags) pass through.</summary>
    public static string T(string? s) => s == null ? "" : _table.TryGetValue(s, out var r) ? r : s;

    /// <summary>"5 items" / "5 элементов". Other languages: "{0} item" / "{0} items" in their file, plus "{0} items (few)" for 2–4 where it differs.</summary>
    public static string Items(int n)
    {
        if (Ru)
        {
            var m10 = n % 10;
            var m100 = n % 100;
            var word = m10 == 1 && m100 != 11 ? "элемент" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "элемента" : "элементов";
            return $"{n} {word}";
        }
        // ponytail: Slavic-style rule (1 / 2–4 / 5+) or one/other; languages with other plural rules get the closest form
        var few = n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 && _table.ContainsKey("{0} items (few)");
        var key = n % 10 == 1 && n % 100 != 11 && (n == 1 || _table.ContainsKey("{0} items (few)")) ? "{0} item" : few ? "{0} items (few)" : "{0} items";
        return F(key, n);
    }

    /// <summary>Translated format string: L.F("Volume {0}%", 80).</summary>
    public static string F(string format, params object?[] args) => string.Format(T(format), args);
}

/// <summary>XAML: Text="{local:T Drop media here}".</summary>
public class TExtension(string text) : MarkupExtension
{
    public TExtension() : this("") { }
    public string Text { get; set; } = text;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
