using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace Pylon;

/// <summary>
/// Updates from GitHub Releases. Copies installed by Velopack update in place with delta packages (only what changed is
/// downloaded) and restart; a copy from the old Inno Setup installer downloads the Velopack setup (that is how it moves
/// over); portable copies get the release page.
/// </summary>
public static class Updater
{
    public const string Repo = "MeowKusTrue/Pylon";
    const string AppId = "{6C7E2B0A-3F4D-4C1B-9E2A-7A51D0C3B9F4}_is1"; // installer/Pylon.iss AppId

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Pylon-updater" }, { "Accept", "application/vnd.github+json" } },
    };

    public record Release(Version Version, string Tag, string Notes, string? SetupUrl, string PageUrl);

    static Velopack.UpdateManager? _velopack;
    static bool _velopackChecked;
    static Velopack.UpdateInfo? _pending;

    /// <summary>Velopack's manager when this copy was installed by it, else null.</summary>
    static Velopack.UpdateManager? Velopack
    {
        get
        {
            if (_velopackChecked) return _velopack;
            _velopackChecked = true;
            try
            {
                // PYLON_UPDATE_FEED: a local folder with packages, for testing updates without GitHub
                var m = Environment.GetEnvironmentVariable("PYLON_UPDATE_FEED") is { Length: > 0 } feed
                    ? new Velopack.UpdateManager(feed)
                    : new Velopack.UpdateManager(new Velopack.Sources.GithubSource($"https://github.com/{Repo}", null, false));
                if (m.IsInstalled) _velopack = m;
            }
            catch (Exception) { } // not a Velopack install (dev build, portable, Inno)
            return _velopack;
        }
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    public static bool IsVelopackInstall => Velopack != null;

    /// <summary>Installed with a setup (vs. the portable zip): only then can we update in place.</summary>
    public static bool IsInstalled => Velopack != null ||
        new[] { Registry.CurrentUser, Registry.LocalMachine }.Any(root =>
            root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}") is { } key
            && key.GetValue("InstallLocation") is string dir
            && Environment.ProcessPath?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The latest release when it's newer than this build, otherwise null. Throws on network errors.</summary>
    public static async Task<Release?> CheckAsync()
    {
        if (Velopack is { } velo)
        {
            _pending = await velo.CheckForUpdatesAsync();
            if (_pending == null) return null;
            var t = _pending.TargetFullRelease;
            var v = new Version(t.Version.Major, t.Version.Minor, t.Version.Patch);
            return new Release(v, $"v{v.ToString(3)}", t.NotesMarkdown ?? "", "velopack", $"https://github.com/{Repo}/releases/tag/v{v.ToString(3)}");
        }
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(Current)) return null;
        var setup = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .FirstOrDefault(a => a.Name.EndsWith("-setup-win-x64.exe", StringComparison.OrdinalIgnoreCase));
        return new Release(version, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            setup.Url is { Length: > 0 } u && u.StartsWith("https://github.com/") ? u : null,
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases");
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Daily background check, when enabled in settings.</summary>
    public static bool DueForCheck()
    {
        if (!Settings.Bool("general.updates")) return false;
        return !DateTime.TryParse(Settings.Str("app.lastupdatecheck"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
               || DateTime.UtcNow - last > TimeSpan.FromDays(1);
    }

    public static void MarkChecked() => Settings.Set("app.lastupdatecheck", DateTime.UtcNow.ToString("o"));

    /// <summary>
    /// Velopack: downloads the update (deltas when possible) and hands it to Velopack's updater, which waits for Pylon to
    /// close, applies it and starts the new version. Inno copies: downloads the setup and starts it silently.
    /// The caller closes Pylon afterwards (so settings and position get saved).
    /// </summary>
    public enum Stage { Downloading, Assembling, Installing }

    /// <summary>Where an update download is: bytes and speed while downloading; Assembling = building the new version from deltas.</summary>
    public record Progress(Stage Stage, double Fraction, long Done, long Total, double BytesPerSecond);

    public static async Task DownloadAndRunAsync(Release r, IProgress<Progress>? progress, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        double speed = 0, lastT = 0;
        long lastBytes = 0;
        void Report(Stage stage, double fraction, long total)
        {
            var done = (long)(fraction * total);
            var t = clock.Elapsed.TotalSeconds;
            if (t - lastT >= 0.5) // a smoothed rate: per-chunk rates jump around
            {
                var now = (done - lastBytes) / (t - lastT);
                speed = speed == 0 ? now : speed * 0.6 + now * 0.4;
                (lastT, lastBytes) = (t, done);
            }
            progress?.Report(new Progress(stage, fraction, done, total, speed));
        }
        if (Velopack is { } velo && _pending != null)
        {
            // Velopack reports 0-70 while it downloads the deltas, then silently rebuilds the full package from them
            // (Update.exe patch, up to a few minutes on a slow disk), then 100. If that fails it downloads the full
            // package from 0 again. The stages are told apart by those numbers.
            var deltas = _pending.DeltasToTarget;
            var full = _pending.TargetFullRelease.Size;
            var viaDeltas = _pending.BaseRelease?.FileName != null && deltas.Length is > 0 and <= 10 && deltas.Sum(d => d.Size) <= full;
            var deltaBytes = deltas.Sum(d => d.Size);
            var last = 0;
            await velo.DownloadUpdatesAsync(_pending, p =>
            {
                if (p < last) { viaDeltas = false; (lastT, lastBytes, speed) = (clock.Elapsed.TotalSeconds, 0, 0); } // fell back to the full package
                last = p;
                if (viaDeltas && p >= 70 && p < 100) progress?.Report(new Progress(Stage.Assembling, 1, deltaBytes, deltaBytes, 0));
                else if (viaDeltas) Report(Stage.Downloading, Math.Min(1, p / 70.0), deltaBytes);
                else Report(Stage.Downloading, p / 100.0, full);
            }, cancel);
            progress?.Report(new Progress(Stage.Installing, 1, 0, 0, 0));
            velo.WaitExitThenApplyUpdates(_pending, silent: true, restart: true);
            return;
        }
        var file = Path.Combine(Path.GetTempPath(), $"Pylon-{r.Version}-setup.exe");
        using (var resp = await Http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(cancel);
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, cancel)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                done += n;
                if (total > 0) Report(Stage.Downloading, (double)done / total, total);
            }
        }
        progress?.Report(new Progress(Stage.Installing, 1, 0, 0, 0));
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }

    public static void OpenPage(Release r) => Process.Start(new ProcessStartInfo(r.PageUrl) { UseShellExecute = true });
}
