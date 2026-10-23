using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace Pylon;

/// <summary>"Only allow one Pylon window": a second launch hands its files to the running player over a named pipe.</summary>
public static class SingleInstance
{
    // a copy on its own profile (PYLON_DATA: tests, a second profile) is a separate player with its own pipe: it never
    // hands its files to, or takes them from, the main one
    static readonly string PipeName = "Pylon-" + Environment.UserName
        + (Environment.GetEnvironmentVariable("PYLON_DATA") is { } data ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(data.ToLowerInvariant())))[..8] : "");
    static Mutex? _mutex;

    /// <summary>Another Pylon of this user is running (the autostart copy then has nothing to do).</summary>
    public static bool IsRunning()
    {
        _mutex = new Mutex(true, PipeName, out var first);
        return !first;
    }

    /// <summary>True when another instance took the files and this process should exit.</summary>
    public static bool ForwardToRunning(string[] args)
    {
        _mutex = new Mutex(true, PipeName, out var first);
        if (first) return false;
        // both are on by default, so only a saved "false" matters; a text search instead of Settings.Load (~90 ms of
        // JSON and the whole settings schema in a process that lives 0.1 s)
        var dir = Environment.GetEnvironmentVariable("PYLON_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pylon");
        string json;
        try { json = File.ReadAllText(Path.Combine(dir, "settings.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { json = ""; }
        if (json.Contains($"\"{(args.Length > 0 ? "library.singlefm" : "library.single")}\": \"false\"")) return false;
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
                        // the file first: libVLC opens it on its own threads while WPF lays out and shows the window
                        if (files.Count > 0) window.Open(files, append: Settings.Bool("library.enqueue"));
                        window.Reveal();
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
