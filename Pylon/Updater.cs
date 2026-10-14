using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace Pylon;

/// <summary>
/// Updates from GitHub Releases: the release workflow publishes Pylon-x.y.z-setup-win-x64.exe on every "v*" tag;
/// installed copies download it and run it silently, portable copies get the release page.
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

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Installed with the setup (vs. the portable zip): only then can we update in place.</summary>
    public static bool IsInstalled =>
        new[] { Registry.CurrentUser, Registry.LocalMachine }.Any(root =>
            root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}") is { } key
            && key.GetValue("InstallLocation") is string dir
            && Environment.ProcessPath?.StartsWith(dir, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The latest release when it's newer than this build, otherwise null. Throws on network errors.</summary>
    public static async Task<Release?> CheckAsync()
    {
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

    /// <summary>Release notes of a published version (null when it has none).</summary>
    public static async Task<string?> NotesForAsync(Version v)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/tags/v{v.ToString(3)}"));
        return doc.RootElement.TryGetProperty("body", out var b) && b.GetString() is { Length: > 0 } s ? s : null;
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

    /// <summary>Downloads the setup and starts it silently; the installer relaunches Pylon when it's done.</summary>
    public static async Task DownloadAndRunAsync(Release r, IProgress<double>? progress)
    {
        var file = Path.Combine(Path.GetTempPath(), $"Pylon-{r.Version}-setup.exe");
        using (var resp = await Http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(file);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }

    public static void OpenPage(Release r) => Process.Start(new ProcessStartInfo(r.PageUrl) { UseShellExecute = true });
}
