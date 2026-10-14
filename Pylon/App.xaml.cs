using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pylon;

public partial class App : Application
{
    void App_Startup(object sender, StartupEventArgs e)
    {
        Settings.Load();
        L.Init();
        if (Environment.GetEnvironmentVariable("PYLON_SNAP") == null && SingleInstance.ForwardToRunning(e.Args))
        {
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}

/// <summary>Dark / Light / System theme plus the accent color, swapped at runtime.</summary>
public static class ThemeManager
{
    public static event Action? Changed;
    public static bool IsLight { get; private set; }
    static bool _watching;

    public static void Apply(string theme, string accentHex)
    {
        IsLight = theme == "Light" || (theme == "System" && SystemUsesLight());
        var res = Application.Current.Resources;
        res.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Colors.{(IsLight ? "Light" : "Dark")}.xaml") };

        var accent = ColorConverter.ConvertFromString(accentHex) is Color c ? c : Color.FromRgb(0xD3, 0x79, 0x18);
        var bg = IsLight ? Colors.White : Colors.Black;
        // the mockup's #AB6314 / #371F06 are #D37918 mixed 19% / 74% toward black
        res["Accent"] = Frozen(accent);
        res["AccentDim"] = Frozen(Mix(accent, bg, IsLight ? 0.12 : 0.19));
        res["AccentBright"] = Frozen(Mix(accent, bg, IsLight ? 0.8 : 0.74));

        Icon.ClearCache();
        Changed?.Invoke();

        if (theme == "System" && !_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && Settings.Str("general.theme") == "System")
                    Application.Current.Dispatcher.BeginInvoke(() => Apply("System", Settings.Str("general.accent")));
            };
        }
    }

    static bool SystemUsesLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v == 1;

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
