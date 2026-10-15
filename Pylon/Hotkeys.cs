using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Pylon;

/// <summary>One bindable action. Default gestures come from the mockup's Keyboard Shortcuts and onboarding screens.</summary>
public record HotkeyAction(string Id, string Group, string Name, string Default, Action<MainWindow> Run);

/// <summary>Configurable shortcuts ("hotkey.&lt;id&gt;" / "ghotkey.&lt;id&gt;" in settings) and system-wide hotkeys.</summary>
public static class Hotkeys
{
    public static readonly List<HotkeyAction> Actions =
    [
        new("fullscreen", "Basic", "Toggle fullscreen", "F", w => w.ToggleFullscreen()),
        new("escape", "Basic", "Exit fullscreen / close dialog", "Escape", w => w.Escape()),
        new("position", "Basic", "Show playback position and time", "T", w => w.ShowPosition()),
        new("fscontrols", "Basic", "Show interface controls in fullscreen", "I", w => w.ShowControlsNow()),
        new("hideui", "Basic", "Hide UI", "Ctrl+Oem5", w => w.ToggleMinimal()),
        new("library", "Basic", "Show / hide file browser", "L", w => w.ToggleLibrary()),
        new("queue", "Basic", "Show / hide queue", "Q", w => w.ToggleQueue()),
        new("miniplayer", "Basic", "Miniplayer", "Ctrl+M", w => w.ToggleMiniplayer()),
        new("search", "Basic", "Search the library", "Ctrl+K", w => w.FocusSearch()),

        new("play", "Playback", "Play / pause", "Space", w => w.TogglePlay()),
        new("stop", "Playback", "Stop playback", "S", w => w.Stop()),
        new("next", "Playback", "Next track", "N", w => w.Next()),
        new("prev", "Playback", "Previous track", "P", w => w.Previous()),
        new("frame", "Playback", "Next frame", "OemPeriod", w => w.NextFrame()),
        new("faster", "Playback", "Faster", "OemPlus", w => w.ChangeRate(+1)),
        new("slower", "Playback", "Slower", "OemMinus", w => w.ChangeRate(-1)),
        new("normalspeed", "Playback", "Normal speed", "Oem2", w => w.ChangeRate(0)),
        new("record", "Playback", "Record", "Shift+R", w => w.ToggleRecord()),
        new("abloop", "Playback", "A-B loop", "Shift+L", w => w.ToggleAbLoop()),
        new("nextchapter", "Playback", "Next chapter", "Shift+N", w => w.JumpChapter(+1)),
        new("prevchapter", "Playback", "Previous chapter", "Shift+P", w => w.JumpChapter(-1)),
        new("bookmark", "Playback", "Add bookmark", "Ctrl+B", w => w.AddBookmark()),
        new("snapshot", "Playback", "Take a snapshot", "Shift+S", w => w.Snapshot()),

        new("fwd1", "Seeking", "Very short jump forward", "Shift+Right", w => w.Skip(0, +1)),
        new("back1", "Seeking", "Very short jump backward", "Shift+Left", w => w.Skip(0, -1)),
        new("fwd2", "Seeking", "Short jump forward", "Right", w => w.Skip(1, +1)),
        new("back2", "Seeking", "Short jump backward", "Left", w => w.Skip(1, -1)),
        new("fwd3", "Seeking", "Medium jump forward", "Ctrl+Right", w => w.Skip(2, +1)),
        new("back3", "Seeking", "Medium jump backward", "Ctrl+Left", w => w.Skip(2, -1)),
        new("fwd4", "Seeking", "Long jump forward", "Ctrl+Alt+Right", w => w.Skip(3, +1)),
        new("back4", "Seeking", "Long jump backward", "Ctrl+Alt+Left", w => w.Skip(3, -1)),

        new("volup", "Audio", "Increase volume", "Up", w => w.NudgeVolume(+1)),
        new("voldown", "Audio", "Decrease volume", "Down", w => w.NudgeVolume(-1)),
        new("mute", "Audio", "Mute", "M", w => w.ToggleMute()),
        new("audiotrack", "Audio", "Cycle audio track", "B", w => w.CycleAudioTrack()),
        new("audiodelayup", "Audio", "Increase audio delay", "K", w => w.ShiftAudioDelay(+0.05)),
        new("audiodelaydown", "Audio", "Decrease audio delay", "J", w => w.ShiftAudioDelay(-0.05)),

        new("subtrack", "Subtitles", "Cycle subtitle track", "V", w => w.CycleSubtitle()),
        new("subdelayup", "Subtitles", "Increase subtitle delay", "H", w => w.ShiftSubtitleDelay(+0.1)),
        new("subdelaydown", "Subtitles", "Decrease subtitle delay", "G", w => w.ShiftSubtitleDelay(-0.1)),

        new("aspect", "Video", "Cycle aspect ratio", "A", w => w.CycleAspect()),
        new("crop", "Video", "Cycle crop", "C", w => w.CycleCrop()),
        new("deinterlace", "Video", "Toggle deinterlacing", "D", w => w.ToggleDeinterlace()),

        new("open", "Media", "Open file(s)", "Ctrl+O", w => w.OpenFiles()),
        new("openfolder", "Media", "Open folder", "Ctrl+F", w => w.OpenFolder()),
        new("openstream", "Media", "Open network stream", "Ctrl+N", w => w.OpenStream()),
        new("convert", "Media", "Convert / save", "Ctrl+R", w => w.OpenConverter()),
        new("opendisc", "Media", "Open disc", "Ctrl+D", w => w.OpenDisc()),
        new("settings", "App", "Settings", "Ctrl+P", w => w.OpenSettings()),
        new("effects", "App", "Effects and filters", "Ctrl+E", w => w.ToggleEffects()),
        new("about", "App", "About Pylon", "Shift+F1", w => w.ShowAbout()),
        new("quit", "App", "Quit", "Ctrl+Q", w => w.Close()),
    ];

    /// <summary>
    /// Layouts people bring from other players. A layout only lists what differs from VLC; the user's own changes go on
    /// top of it, and "Reset" returns to the layout. "A|B" = either key.
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> Layouts = new()
    {
        ["VLC-style"] = [],
        ["YouTube-style"] = new()
        {
            ["play"] = "Space|K", ["back2"] = "J", ["fwd2"] = "L", ["back1"] = "Left", ["fwd1"] = "Right", ["back3"] = "Ctrl+Left", ["fwd3"] = "Ctrl+Right",
            ["audiodelaydown"] = "Shift+J", ["audiodelayup"] = "Shift+K", ["library"] = "Ctrl+L",
            ["subtrack"] = "C", ["crop"] = "Shift+C", ["next"] = "Shift+N", ["prev"] = "Shift+P", ["nextchapter"] = "Ctrl+Shift+N", ["prevchapter"] = "Ctrl+Shift+P",
            ["faster"] = "Shift+OemPeriod", ["slower"] = "Shift+OemComma", ["frame"] = "OemPeriod",
        },
        ["MPC-HC-style"] = new()
        {
            ["fullscreen"] = "Alt+Return|F", ["stop"] = "OemPeriod", ["frame"] = "Ctrl+Right", ["back1"] = "Alt+Left", ["fwd1"] = "Alt+Right",
            ["back2"] = "Left", ["fwd2"] = "Right", ["back3"] = "Shift+Left", ["fwd3"] = "Shift+Right",
            ["next"] = "Next", ["prev"] = "Prior", ["mute"] = "Ctrl+M|M", ["miniplayer"] = "Ctrl+Shift+M", ["snapshot"] = "Alt+I",
            ["faster"] = "Ctrl+Up", ["slower"] = "Ctrl+Down", ["quit"] = "Alt+X|Ctrl+Q",
        },
    };

    /// <summary>What a shortcut is without the user's changes: the chosen layout, else Pylon's own (VLC's).</summary>
    public static string Base(string id) =>
        Layouts.GetValueOrDefault(Settings.Str("controls.defaults"))?.GetValueOrDefault(id) ?? Actions.First(a => a.Id == id).Default;

    public static string Gesture(string id) => Settings.Str("hotkey." + id) is { Length: > 0 } g ? g : Base(id);
    public static string GlobalGesture(string id) => Settings.Str("ghotkey." + id);

    public static void ResetAll()
    {
        foreach (var a in Actions) { Settings.Set("hotkey." + a.Id, ""); Settings.Set("ghotkey." + a.Id, ""); }
    }

    /// <summary>"Ctrl+Shift+S" for a key event (Key.System is resolved).</summary>
    public static string FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return "";
        return Compose(Keyboard.Modifiers, key);
    }

    public static string Compose(ModifierKeys m, Key key)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key == Key.Add ? "OemPlus" : key == Key.Subtract ? "OemMinus" : key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Human label: "Ctrl + ←".</summary>
    public static string Display(string gesture) => string.IsNullOrEmpty(gesture) ? "" : gesture.Contains('|') ? string.Join(" / ", gesture.Split('|').Select(Display)) : string.Join(" + ", gesture.Split('+').Select(p => p switch
    {
        "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "Escape" => "Esc", "Oem5" => "\\", "OemPeriod" => ".", "OemComma" => ",",
        "OemPlus" => "+", "OemMinus" => "-", "Oem2" => "/", "Return" => "Enter", "Next" => "PgDn", "Prior" => "PgUp",
        _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
        _ => p,
    }));

    public static HotkeyAction? Match(string gesture) => gesture.Length == 0 ? null : Actions.FirstOrDefault(a => Gesture(a.Id).Split('|').Contains(gesture));

    // ───────── system-wide hotkeys (RegisterHotKey) ─────────

    static HwndSource? _source;
    static readonly Dictionary<int, HotkeyAction> Registered = new();

    public static void RegisterGlobal(MainWindow w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (_source == null)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0x0312 /*WM_HOTKEY*/ && Registered.TryGetValue(wp.ToInt32(), out var a)) { a.Run(w); handled = true; }
                return IntPtr.Zero;
            });
        }
        foreach (var id in Registered.Keys) UnregisterHotKey(hwnd, id);
        Registered.Clear();
        var next = 1;
        foreach (var a in Actions)
        {
            if (GlobalGesture(a.Id) is not { Length: > 0 } g || !TryParse(g, out var mods, out var key)) continue;
            uint m = (mods.HasFlag(ModifierKeys.Alt) ? 1u : 0) | (mods.HasFlag(ModifierKeys.Control) ? 2u : 0) | (mods.HasFlag(ModifierKeys.Shift) ? 4u : 0) | 0x4000 /*NOREPEAT*/;
            if (RegisterHotKey(hwnd, next, m, (uint)KeyInterop.VirtualKeyFromKey(key))) Registered[next++] = a;
        }
    }

    static bool TryParse(string g, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        foreach (var p in g.Split('+'))
        {
            if (p == "Ctrl") mods |= ModifierKeys.Control;
            else if (p == "Alt") mods |= ModifierKeys.Alt;
            else if (p == "Shift") mods |= ModifierKeys.Shift;
            else if (!Enum.TryParse(p, out key)) return false;
        }
        return key != Key.None;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
