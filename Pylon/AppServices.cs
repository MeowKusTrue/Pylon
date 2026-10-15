using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace Pylon;

/// <summary>"Only allow one Pylon window": a second launch hands its files to the running player over a named pipe.</summary>
public static class SingleInstance
{
    static readonly string PipeName = "Pylon-" + Environment.UserName;
    static Mutex? _mutex;

    /// <summary>True when another instance took the files and this process should exit.</summary>
    public static bool ForwardToRunning(string[] args)
    {
        _mutex = new Mutex(true, PipeName, out var first);
        if (first) return false;
        var wanted = args.Length > 0 ? Settings.Bool("library.singlefm") : Settings.Bool("library.single");
        if (!wanted) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var w = new StreamWriter(pipe);
            foreach (var a in args) w.WriteLine(a.Contains("://") ? a : Path.GetFullPath(a));
            w.WriteLine(); // end of message (also just brings the window forward when empty)
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; } // can't reach that copy: just start
    }

    /// <summary>Lets a fresh copy of the app start while this one is still closing (language change, updates).</summary>
    public static void Release()
    {
        _mutex?.Dispose();
        _mutex = null;
    }

    public static void Listen(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    var files = new List<string>();
                    while (r.ReadLine() is { Length: > 0 } line) files.Add(line);
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                        window.Activate();
                        if (files.Count > 0) window.Open(files, append: Settings.Bool("library.enqueue"));
                    });
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { return; } // another Pylon owns the pipe (single window turned off): this copy just doesn't listen
            }
        }) { IsBackground = true, Name = "single-instance pipe" };
        thread.Start();
    }
}

/// <summary>Playback profiles from the mockup's main menu ("Profile (Movies)"): named bundles of settings.</summary>
public static class Profiles
{
    public static readonly string[] Names = ["Default", "Movies", "Music", "Night"];

    static readonly Dictionary<string, (string Key, string Value)[]> Sets = new()
    {
        ["Movies"] = [("subs.enable", "true"), ("video.deint", "Automatic"), ("playback.resume", "Ask"), ("ui.autohide", "In fullscreen only"), ("audio.night", "false")],
        ["Music"] = [("audio.normalize", "true"), ("ui.cover", "true"), ("playback.resume", "Never"), ("ui.scene", "Bio"), ("playback.autoplay", "true")],
        ["Night"] = [("audio.night", "true"), ("audio.normalize", "true"), ("general.theme", "Dark"), ("video.screensaver", "true")],
    };

    public static void Apply(string name)
    {
        // back to defaults for everything any profile touches, then layer the chosen one
        foreach (var key in Sets.Values.SelectMany(s => s).Select(s => s.Key).Distinct())
            Settings.Set(key, Settings.Find(key)?.Default ?? "");
        foreach (var (key, value) in Sets.GetValueOrDefault(name) ?? []) Settings.Set(key, value);
        Settings.Set("general.profile", name);
    }
}
