using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pylon;

public partial class App : Application
{
    void App_Startup(object sender, StartupEventArgs e)
    {
        // build step: let libVLC write plugins.dat next to its plugins, so every later start skips the plugin scan
        if (e.Args is ["--build-plugin-cache"])
        {
            LibVLCSharp.Shared.Core.Initialize();
            using (new LibVLCSharp.Shared.LibVLC("--reset-plugins-cache", "--quiet")) { }
            Shutdown();
            return;
        }
        Perf.Mark("startup");
        if (!StartupOptions.SafeMode && StartupOptions.Arguments.Length > 0) GpuWarmup.Start();
        ErrorLog.Install(this);
        SmoothScroll.Install();
        Motion.HookMenus();
        Motion.HookPress();
        Motion.HookSwitches();
        while (true)
        {
            var document = "settings.json";
            try
            {
                System.IO.Directory.CreateDirectory(Library.Dir);
                Settings.Load();
                document = "library.json";
                Library.Load();
                document = "";
                var probe = System.IO.Path.Combine(Library.Dir, ".pylon-profile-" + Guid.NewGuid().ToString("N"));
                using (new System.IO.FileStream(probe, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write,
                    System.IO.FileShare.None, 1, System.IO.FileOptions.DeleteOnClose)) { }
                break;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                L.Init();
                if (document.Length > 0 && ProfileBackups.Latest(document) is { } backup)
                {
                    var answer = MessageBox.Show(L.F("Could not read {0}. A backup is available.\nYes: restore the backup and keep the damaged original.\nNo: choose another profile.\nCancel: exit.", document),
                        L.T("Profile recovery"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Cancel) { Shutdown(); return; }
                    if (answer == MessageBoxResult.Yes)
                    {
                        try { ProfileBackups.Restore(document, backup); continue; }
                        catch (Exception restore) when (restore is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                        { MessageBox.Show(restore.Message, L.T("Profile recovery"), MessageBoxButton.OK, MessageBoxImage.Error); }
                    }
                }
                var message = L.T("Pylon could not read this profile. The original files have not been changed.") + "\n\n"
                    + Library.Dir + "\n\n" + error.Message + "\n\n" + L.T("Choose another profile folder to continue, or cancel to exit.");
                if (MessageBox.Show(message, L.T("Profile error"), MessageBoxButton.OKCancel, MessageBoxImage.Error) != MessageBoxResult.OK)
                { Shutdown(); return; }
                var folder = new OpenFolderDialog();
                if (folder.ShowDialog() != true) { Shutdown(); return; }
                SingleInstance.Release();
                Library.Dir = folder.FolderName;
                Environment.SetEnvironmentVariable("PYLON_DATA", Library.Dir);
                if (SingleInstance.IsRunning())
                { MessageBox.Show(L.T("Pylon is already running with the selected profile."), "Pylon"); Shutdown(); return; }
            }
        }
        InterfaceRendering.Apply();
        Settings.Changed += key => { if (key is "perf.softwareui" or "*") InterfaceRendering.Apply(); };
        InterfaceRendering.NoteStartup();
        L.Init();
        PowerPolicy.Initialize();
        Accessibility.Install();
        Perf.Mark("settings + strings");
        SessionEnding += (_, _) => (MainWindow as Pylon.MainWindow)?.Quit();
        Pylon.MainWindow.PrewarmEngine();
        new MainWindow().Show();
    }
}

/// <summary>
/// The safety net: an exception nobody caught (an async void handler, a timer, a menu click) used to close the player
/// without a word. Now it goes to errors.log in the profile folder, the player says so in a line, and keeps running.
/// Only running out of memory is let through: nothing sane can follow it. A burst of the same error (one thrown every
/// frame) is written and shown once.
/// </summary>
public static class ErrorLog
{
    static string FilePath => System.IO.Path.Combine(Library.Dir, "errors.log");
    static readonly Dictionary<string, DateTime> Recent = [];

    /// <summary>The main window shows the line (set by it; null until it exists).</summary>
    public static Action<string>? Show;

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception, "UI");
            e.Handled = e.Exception is not OutOfMemoryException;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "task"); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Write(ex, "fatal"); }; // can't be stopped: at least it's written
    }

    /// <summary>A note for diagnosing (no popup), in the same file.</summary>
    public static void Note(string where, string text)
    {
        lock (Recent)
        try
        {
            System.IO.Directory.CreateDirectory(Library.Dir);
            var file = new System.IO.FileInfo(FilePath);
            if (file.Exists && file.Length > 1 << 20) file.Delete();
            System.IO.File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] Pylon {Updater.Current.ToString(3)}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception io) when (io is System.IO.IOException or UnauthorizedAccessException) { }
    }

    public static void Write(Exception e, string where)
    {
        var key = e.GetType().FullName + e.StackTrace?.Split('\n').FirstOrDefault();
        lock (Recent)
        {
            if (Recent.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return;
            Recent[key] = DateTime.UtcNow;
        }
        Note(where, e.ToString());
        if (where != "fatal")
            Application.Current?.Dispatcher.BeginInvoke(() => Show?.Invoke(L.F("Something went wrong ({0}). Pylon kept going; details are in errors.log.", e.GetType().Name)));
    }
}

/// <summary>Dark / Light / System theme plus the accent color, swapped at runtime.</summary>
public static class ThemeManager
{
    public static event Action? Changed;
    public static bool IsLight { get; private set; }
    static bool _watching;

    /// <summary>Extra themes: a base palette (Dark/Light) with some tokens recolored.</summary>
    static readonly Dictionary<string, (bool Light, string Tokens)> Presets = new()
    {
        ["OLED black"] = (false, "BgA3=#000000 BgA4=#050505 BgB1=#000000 BgB2=#000000 BgRaised=#141414 FgB1=#101010 DividerBright=#262626 DividerDim=#121212 MenuBg=#F20A0A0A PanelBg=#F2050505 ThumbBg=#1E1E1E"),
        ["Midnight"] = (false, "BgA3=#141A26 BgA4=#121826 BgB1=#0C111B BgB2=#080C14 BgRaised=#1C2433 FgA0=#C9D3E3 FgA1=#93A0B5 FgA2=#8794A8 FgA3=#5F6B80 FgA4=#36405A FgB1=#0F1420 OnAccent=#0F1420 DividerBright=#2A3448 DividerDim=#1A2130 MenuBg=#F2121826 PanelBg=#F20C111B ThumbBg=#263045 StageBg=#080C14"),
        ["Nord"] = (false, "BgA3=#2E3440 BgA4=#2B313C BgB1=#242933 BgB2=#1F232B BgRaised=#3B4252 FgA0=#ECEFF4 FgA1=#D8DEE9 FgA2=#C0C8D6 FgA3=#8F9BB0 FgA4=#4C566A FgB1=#2E3440 OnAccent=#2E3440 DividerBright=#434C5E DividerDim=#353C4A Hover=#12FFFFFF MenuBg=#F22E3440 PanelBg=#F2242933 ThumbBg=#434C5E StageBg=#20242C"),
        ["Sepia"] = (true, "BgA3=#F4ECDD BgA4=#EDE3D0 BgB1=#FBF6EC BgB2=#E6DAC3 BgRaised=#EADFCB FgA0=#3B2F22 FgA1=#5C4B38 FgA2=#67563F FgA3=#9A8669 FgA4=#C9B99D FgB1=#E0D3BB DividerBright=#D6C7AB DividerDim=#E6DAC5 MenuBg=#F7FBF6EC PanelBg=#F7F4ECDD StageBg=#E9DFCC ThumbBg=#DCCDB2"),
    };

    public static readonly string[] Themes = ["Dark", "Light", "System", .. Presets.Keys];

    public static void Apply(string theme, string accentHex)
    {
        var preset = Presets.GetValueOrDefault(theme);
        IsLight = preset.Tokens != null ? preset.Light : theme == "Light" || (theme == "System" && SystemUsesLight());
        var res = Application.Current.Resources;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Colors.{(IsLight ? "Light" : "Dark")}.xaml") };
        foreach (var token in preset.Tokens?.Split(' ') ?? [])
            palette[token[..token.IndexOf('=')]] = Frozen((Color)ColorConverter.ConvertFromString(token[(token.IndexOf('=') + 1)..]));
        res.MergedDictionaries[0] = palette;

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

    public static bool SystemUsesLight() =>
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
