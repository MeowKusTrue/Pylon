using System.IO;
using System.Runtime.InteropServices;

namespace Pylon;

public static class StartupOptions
{
    public static bool SafeMode { get; private set; }
    public static bool Portable { get; private set; }
    public static string[] Arguments { get; private set; } = [];
    public static string ToolsDirectory => Portable ? Path.Combine(Library.Dir, "tools")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools");

    public static string? PortableData(string folder, bool requested) => requested || File.Exists(Path.Combine(folder, "portable.txt"))
        ? Path.Combine(folder, "data") : null;

    public static void Initialize(string[] args)
    {
        Portable = false;
        var maintenance = args.Any(a => a.StartsWith("--veloapp", StringComparison.Ordinal) || a == "--build-plugin-cache");
        SafeMode = !maintenance && (args.Contains("--safe-mode") || Environment.GetEnvironmentVariable("PYLON_SAFE_MODE") == "1"
            || Environment.GetEnvironmentVariable("PYLON_DATA") == null && (GetAsyncKeyState(0x10) & 0x8000) != 0);
        Arguments = args.Where(a => a is not ("--portable" or "--safe-mode")).ToArray();
        if (PortableData(AppContext.BaseDirectory, args.Contains("--portable")) is { } data)
        {
            Portable = true;
            if (args.Contains("--portable")) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "portable.txt"), "Pylon portable profile: data\n");
            if (Environment.GetEnvironmentVariable("PYLON_DATA") == null) Environment.SetEnvironmentVariable("PYLON_DATA", data);
        }
    }

    public static string? Override(string key) => !SafeMode ? null : key switch
    {
        "perf.softwareui" or "ui.lite" => "true",
        "perf.hw" => "Off",
        "video.hdr" => "Never",
        "playback.ownengine" or "playback.autoplay" or "playback.crossfade" or "audio.eq" or "audio.normalize"
            or "audio.night" or "audio.compressor" or "audio.spatial" or "audio.chorus" or "audio.karaoke"
            or "general.background" or "remote.auto" => "false",
        "ui.scene" or "ui.spectrumstyle" or "audio.replaygain" => "Off",
        _ => null,
    };

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
