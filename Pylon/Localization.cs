using System.Globalization;
using System.Windows.Markup;

namespace Pylon;

/// <summary>
/// UI language. Texts are written in English in code and looked up in <see cref="Strings.Ru"/> when Russian is active
/// (Settings → General → Interface language: System / English / Русский). Applied at startup.
/// </summary>
public static class L
{
    public static bool Ru { get; private set; }

    public static void Init()
    {
        Ru = Settings.Str("general.language") switch
        {
            "Русский" => true,
            "English" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru",
        };
    }

    /// <summary>Translation of an exact English UI string; unknown strings (file names, tags) pass through.</summary>
    public static string T(string? s) => s == null ? "" : Ru && Strings.Ru.TryGetValue(s, out var r) ? r : s;

    /// <summary>"5 items" / "5 элементов" (Russian plural forms).</summary>
    public static string Items(int n)
    {
        if (!Ru) return n == 1 ? "1 item" : $"{n} items";
        var m10 = n % 10;
        var m100 = n % 100;
        var word = m10 == 1 && m100 != 11 ? "элемент" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "элемента" : "элементов";
        return $"{n} {word}";
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
