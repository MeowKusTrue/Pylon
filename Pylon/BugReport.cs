using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace Pylon;

/// <summary>
/// "Report a Problem…": a zip on the desktop with what helps to find a bug (versions, GPU, what's playing — codecs, not
/// the file's name —, settings without keys, tokens or accounts, the error log) and a GitHub issue form filled with the
/// basics; the zip is shown in Explorer so it can be dropped onto the form. Nothing is sent by Pylon itself.
/// </summary>
public static class BugReport
{
    // settings that may hold secrets or identify the user: left out of the report
    static readonly Regex Secret = new("key|token|secret|pass|cookie|session|auth|user|email|account|proxy|clientid|code", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Open(Window owner, string? playing, MediaPlayer mp, string vlcVersion)
    {
        if (MessageBox.Show(owner, L.T("Pylon will put a report on your desktop (versions, graphics card, the codecs of what's playing, settings without keys or accounts, recent errors) and open GitHub with a filled-in form. Attach the report there. Nothing is sent without you."),
                L.T("Report a Problem"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        var text = Build(playing, mp, vlcVersion);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        var zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Pylon-report-{stamp}.zip");
        try
        {
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("report.txt").Open(), Encoding.UTF8)) w.Write(text);
                foreach (var log in new[] { Path.Combine(Library.Dir, "errors.log"), Settings.Bool("general.log") ? Settings.Str("general.logfile") : "" })
                    if (log.Length > 0 && File.Exists(log)) AddTail(z, log, 400);
            }
            Process.Start("explorer.exe", $"/select,\"{zip}\"");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { zip = ""; }
        var summary = string.Join("\n", text.Split('\n').TakeWhile(l => l.Length > 0)); // the first block: versions and system
        var body = $"**What happened?**\n\n\n**What did you expect?**\n\n\n**Steps to reproduce**\n1. \n\n---\n{summary}\n\n" +
                   (zip.Length > 0 ? $"_Drop {Path.GetFileName(zip)} from your desktop here._" : "");
        var url = "https://github.com/MeowKusTrue/Pylon/issues/new?title=" + Uri.EscapeDataString("Problem: ") + "&body=" + Uri.EscapeDataString(body);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    static void AddTail(ZipArchive z, string path, int lines)
    {
        try
        {
            var all = File.ReadAllLines(path);
            using var w = new StreamWriter(z.CreateEntry(Path.GetFileName(path)).Open(), Encoding.UTF8);
            foreach (var l in all.Skip(Math.Max(0, all.Length - lines))) w.WriteLine(Regex.Replace(l, @"[A-Za-z]:\[^""<>|\r\n]*", "<path>")); // no file names
        }
        catch (IOException) { }
    }

    static string Build(string? playing, MediaPlayer mp, string vlcVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pylon {Updater.Current.ToString(3)} ({(Updater.IsVelopackInstall ? "installed" : "portable/dev")}), libVLC {vlcVersion}");
        using (var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            sb.AppendLine($"Windows {nt?.GetValue("DisplayVersion")} build {Environment.OSVersion.Version.Build}.{nt?.GetValue("UBR")}, {Environment.ProcessorCount} threads, {Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0)} GB RAM");
        sb.AppendLine("GPU: " + string.Join(", ", Gpus()));
        sb.AppendLine($"Language {L.Code}, theme {Settings.Str("general.theme")}, output {AudioChain.Output}");
        sb.AppendLine();
        if (playing != null)
        {
            sb.AppendLine($"Playing: {(playing.Contains("://") ? new Uri(playing).Host : Path.GetExtension(playing))} at {mp.Time / 1000}s of {mp.Length / 1000}s, {(mp.IsPlaying ? "playing" : "paused")}, rate {mp.Rate}");
            foreach (var t in mp.Media?.Tracks ?? [])
                sb.AppendLine(t.TrackType switch
                {
                    TrackType.Video => $"  video {Fourcc(t.Codec)} {t.Data.Video.Width}x{t.Data.Video.Height} {(t.Data.Video.FrameRateDen > 0 ? t.Data.Video.FrameRateNum / (double)t.Data.Video.FrameRateDen : 0):0.##} fps",
                    TrackType.Audio => $"  audio {Fourcc(t.Codec)} {t.Data.Audio.Channels} ch {t.Data.Audio.Rate} Hz",
                    _ => $"  {t.TrackType.ToString().ToLowerInvariant()} {Fourcc(t.Codec)}",
                });
            sb.AppendLine();
        }
        sb.AppendLine("Settings changed from the defaults:");
        foreach (var (k, v) in Settings.Changed_.OrderBy(p => p.Key))
            sb.AppendLine(Secret.IsMatch(k) ? $"  {k} = (left out)" : $"  {k} = {(v.Length > 120 ? v[..120] + "…" : Regex.Replace(v, @"[A-Za-z]:\[^""<>|]*", "<path>"))}");
        return sb.ToString();
    }

    static string Fourcc(uint c) => Encoding.ASCII.GetString(BitConverter.GetBytes(c)).Trim('\0', ' ');

    internal static IEnumerable<string> Gpus()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) yield break;
        foreach (var n in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(n);
            if (k?.GetValue("DriverDesc") is string d) yield return $"{d} (driver {k.GetValue("DriverVersion")})";
        }
    }
}
