using System.Diagnostics;
using System.IO;

namespace Pylon;

/// <summary>Startup timing: PYLON_PROFILE=file.txt appends "ms since process start  stage" lines.</summary>
public static class Perf
{
    static readonly string? File = Environment.GetEnvironmentVariable("PYLON_PROFILE");
    static readonly object Gate = new();

    public static bool Enabled => File != null;

    public static void Mark(string stage)
    {
        if (File == null) return;
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        lock (Gate) System.IO.File.AppendAllText(File, $"{ms,7:0} ms  {stage}{Environment.NewLine}"); // the video engine marks from several threads
    }
}
