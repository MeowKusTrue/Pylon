using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pylon;

/// <summary>
/// What the Inno installer used to write, now done by the app from Velopack's install / update / uninstall hooks (per
/// user, HKCU): ProgIDs with our file icons, "Open with", the Default Apps entry and "Play with Pylon" on folders.
/// It never takes over an extension by itself — Windows asks the user (Settings → Default apps).
/// </summary>
public static partial class ShellRegistration
{
    const string Classes = @"Software\Classes";
    const string Capabilities = @"Software\Pylon\Capabilities";

    /// <summary>Velopack's launcher next to "current\": its path doesn't change from one update to the next.</summary>
    static string Exe
    {
        get
        {
            var dir = AppContext.BaseDirectory.TrimEnd('\\');
            var stub = Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Pylon.exe");
            return Path.GetFileName(dir).Equals("current", StringComparison.OrdinalIgnoreCase) && File.Exists(stub) ? stub : Environment.ProcessPath!;
        }
    }

    public static void Register()
    {
        var exe = Exe;
        var icons = Path.Combine(AppContext.BaseDirectory, "FileIcons");
        var open = $"\"{exe}\" \"%1\"";
        var ru = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        using var hkcu = Registry.CurrentUser;

        // one ProgID per format, so each one shows its own icon (Explorer still draws video frames / album art on top)
        foreach (var (ext, id, video) in Formats())
        {
            using var k = hkcu.CreateSubKey($@"{Classes}\{id}");
            k.SetValue("", $"{ext.TrimStart('.').ToUpperInvariant()} {(video ? (ru ? "видео" : "video") : (ru ? "аудио" : "audio"))} (Pylon)");
            var icon = Path.Combine(icons, ext.TrimStart('.') + ".ico");
            using (var di = k.CreateSubKey("DefaultIcon")) di.SetValue("", File.Exists(icon) ? icon : Path.Combine(icons, video ? "file-video.ico" : "file-audio.ico"));
            // over a thumbnail (video frame, album art) Explorer draws this badge instead of the whole file icon
            k.SetValue("TypeOverlay", $"{exe},0");
            if (video) k.SetValue("Treatment", 3, RegistryValueKind.DWord); // film sprockets
            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
        }

        using (var app = hkcu.CreateSubKey($@"{Classes}\Applications\Pylon.exe"))
        {
            app.SetValue("FriendlyAppName", "Pylon");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions()) types.SetValue(ext.Ext, "");
        }
        using (var caps = hkcu.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", "Pylon");
            caps.SetValue("ApplicationDescription", ru ? "Медиаплеер на движке libVLC." : "A media player built on libVLC.");
            caps.SetValue("ApplicationIcon", $"{exe},0");
            using var fa = caps.CreateSubKey("FileAssociations");
            foreach (var (ext, id) in Extensions()) fa.SetValue(ext, id);
        }
        using (var reg = hkcu.CreateSubKey(@"Software\RegisteredApplications")) reg.SetValue("Pylon", Capabilities);
        foreach (var (ext, id) in Extensions())
        {
            using var k = hkcu.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids");
            k.SetValue(id, Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var folder = hkcu.CreateSubKey($@"{Classes}\Directory\shell\Pylon.Play"))
        {
            folder.SetValue("", ru ? "Воспроизвести в Pylon" : "Play with Pylon");
            folder.SetValue("Icon", $"{exe},0");
            using var cmd = folder.CreateSubKey("command");
            cmd.SetValue("", open);
        }
        // pylon://join/?url=… from a Watch Together room's page ("Open in Pylon")
        using (var proto = hkcu.CreateSubKey($@"{Classes}\pylon"))
        {
            proto.SetValue("", "URL:Pylon");
            proto.SetValue("URL Protocol", "");
            using var cmd = proto.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", open);
        }
        // "Add to Music" on every audio format, whatever app opens it: a submenu with the music folders
        foreach (var ext in MainWindow.AudioExt)
        {
            using var k = hkcu.CreateSubKey($@"{Classes}\SystemFileAssociations\{ext}\shell\Pylon.AddToMusic");
            k.SetValue("MUIVerb", ru ? "Добавить в музыку" : "Add to Music");
            k.SetValue("Icon", $"{exe},0");
            k.SetValue("ExtendedSubCommandsKey", "Pylon.MusicMenu");
        }
        _musicMenuFor = null;
        MusicMenu();
        Notify();
    }

    static string? _musicMenuFor;

    /// <summary>
    /// The folders under "Add to Music" in Explorer, rewritten when the library's music folders change. Each one runs
    /// "Pylon.exe --add-to-music folder file" (one quick copy per selected file, no window). Only where Pylon is registered
    /// (an installed copy), never from a test profile: a portable or developer build leaves the registry alone.
    /// </summary>
    public static void MusicMenu()
    {
        var targets = Library.MusicTargets();
        var key = string.Join("|", targets);
        if (key == _musicMenuFor || Environment.GetEnvironmentVariable("PYLON_DATA") != null) return;
        _musicMenuFor = key;
        using var hkcu = Registry.CurrentUser;
        using (var caps = hkcu.OpenSubKey(Capabilities)) if (caps == null) return;
        hkcu.DeleteSubKeyTree($@"{Classes}\Pylon.MusicMenu", throwOnMissingSubKey: false);
        for (var i = 0; i < targets.Count; i++)
        {
            var dir = targets[i].TrimEnd('\\'); // "D:\" would escape the closing quote ("D:" is made a root again on the way in)
            using var k = hkcu.CreateSubKey($@"{Classes}\Pylon.MusicMenu\shell\{i:00}");
            k.SetValue("MUIVerb", Path.GetFileName(dir) is { Length: > 0 } name ? $"{name}   ({dir})" : dir);
            k.SetValue("MultiSelectModel", "Player"); // more than 15 files at once
            using var cmd = k.CreateSubKey("command");
            cmd.SetValue("", $"\"{Exe}\" --add-to-music \"{dir}\" \"%1\"");
        }
    }

    public static void Unregister()
    {
        using var hkcu = Registry.CurrentUser;
        foreach (var key in new[] { $@"{Classes}\Pylon.Video", $@"{Classes}\Pylon.Audio", $@"{Classes}\Applications\Pylon.exe", $@"{Classes}\Directory\shell\Pylon.Play", $@"{Classes}\Pylon.MusicMenu", $@"{Classes}\pylon", @"Software\Pylon" }
                     .Concat(Formats().Select(f => $@"{Classes}\{f.Id}"))
                     .Concat(MainWindow.AudioExt.Select(e => $@"{Classes}\SystemFileAssociations\{e}\shell\Pylon.AddToMusic")))
            hkcu.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using (var reg = hkcu.OpenSubKey(@"Software\RegisteredApplications", writable: true)) reg?.DeleteValue("Pylon", throwOnMissingValue: false);
        foreach (var (ext, id) in Extensions())
        {
            // the defaults the big button set: an extension left pointing at a deleted ProgID would open with nothing
            using (var k = hkcu.OpenSubKey($@"{Classes}\{ext}", writable: true))
                if (k?.GetValue("") is string v && v == id) k.DeleteValue("");
            using (var k = hkcu.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", writable: true))
            {
                k?.DeleteValue(id, throwOnMissingValue: false);
                k?.DeleteValue("Pylon.Audio", throwOnMissingValue: false); // entries written by 1.0-1.2
                k?.DeleteValue("Pylon.Video", throwOnMissingValue: false);
            }
        }
        Notify();
    }

    const string InnoKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C7E2B0A-3F4D-4C1B-9E2A-7A51D0C3B9F4}_is1";

    /// <summary>A per-user copy from the old Inno Setup installer is still there.</summary>
    public static bool HasInnoInstall
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(InnoKey); return k != null; }
    }

    /// <summary>
    /// After moving from the Inno installer: remove that copy (its files, shortcuts and registry) silently.
    /// Its uninstaller also deletes our ProgIDs, so they are written again afterwards.
    /// </summary>
    public static void RemoveInnoInstall()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(InnoKey)) // per-user installs only: an all-users one would need admin rights
        {
            if (k?.GetValue("UninstallString") is not string uninstall) return;
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Pylon.lnk");
            var hadDesktop = File.Exists(desktop); // Velopack's shortcut: the old uninstaller deletes one with the same name
            // the old Start menu entry: pins to Start / the taskbar point at it, so it comes back aimed at the new copy
            var startEntry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Pylon", "Pylon.lnk");
            var hadStartEntry = File.Exists(startEntry);
            try
            {
                using var p = Process.Start(new ProcessStartInfo(uninstall.Trim('"'), "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                p?.WaitForExit(60_000);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return; }
            if (hadDesktop && !File.Exists(desktop)) Shortcut(desktop);
            if (hadStartEntry && !File.Exists(startEntry)) { Directory.CreateDirectory(Path.GetDirectoryName(startEntry)!); Shortcut(startEntry); }
        }
        Register(); // the old uninstaller removed the shared registry keys too
    }

    static void Shortcut(string path)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(path);
            link.TargetPath = Exe;
            link.IconLocation = Exe + ",0";
            link.Save();
        }
        catch (Exception) { } // a missing shortcut is not worth failing the start over
    }

    static IEnumerable<(string Ext, string Id, bool Video)> Formats() =>
        MainWindow.VideoExt.Select(e => (e, "Pylon" + e, true)).Concat(MainWindow.AudioExt.Select(e => (e, "Pylon" + e, false)));

    static IEnumerable<(string Ext, string Id)> Extensions() => Formats().Select(f => (f.Ext, f.Id));

    // ───────── default player: the hybrid way ─────────

    /// <summary>How many of Pylon's formats open in Pylon right now (Windows' own answer, whoever set it).</summary>
    public static (int Ours, int Total) DefaultStatus()
    {
        var all = Extensions().ToList();
        return (all.Count(e => IsOurs(ProgIdOf(e.Ext))), all.Count);
    }

    static bool IsOurs(string? progId) => progId?.StartsWith("Pylon.", StringComparison.OrdinalIgnoreCase) == true;

    static string? ProgIdOf(string ext)
    {
        var buf = new System.Text.StringBuilder(260);
        uint n = 260;
        return AssocQueryString(0, 20 /*ASSOCSTR_PROGID*/, ext, null, buf, ref n) == 0 ? buf.ToString() : null;
    }

    /// <summary>Of Pylon's formats, the ones that don't open in Pylon yet.</summary>
    public static List<string> NotOurs(Func<string, string?> progIdOf) =>
        [.. Extensions().Select(e => e.Ext).Where(ext => !IsOurs(progIdOf(ext)))];

    /// <summary>
    /// The big button, done the way 7-Zip and K-Lite do it, because the user asked for it: for every format that doesn't
    /// open in Pylon, the app chosen for it (UserChoice, and UserChoiceLatest on newer Windows 11; their hashes can't be
    /// written, but the keys can be removed) is cleared, the extension's default in the user's own classes points at
    /// Pylon, and Windows' "new app available" toast for it is marked as seen. Anything Windows still protects (it can
    /// block some types) is left to its default-apps page, which then opens on Pylon. Returns how many formats open in
    /// Pylon afterwards, and whether that page was opened.
    /// </summary>
    public static (int Ours, int Total, bool OpenedSettings) MakeDefault()
    {
        Register(); // ProgIDs and icons up to date first
        using var hkcu = Registry.CurrentUser;
        foreach (var ext in NotOurs(ProgIdOf))
        {
            var fileExts = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}";
            foreach (var choice in new[] { "UserChoice", "UserChoiceLatest" })
                try { hkcu.DeleteSubKeyTree($@"{fileExts}\{choice}", throwOnMissingSubKey: false); }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { } // protected: settings page
            using (var k = hkcu.CreateSubKey($@"{Classes}\{ext}")) k.SetValue("", "Pylon" + ext);
            using (var toasts = hkcu.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts"))
                toasts.SetValue($"Pylon{ext}_{ext}", 0, RegistryValueKind.DWord);
        }
        Notify();
        var (ours, total) = DefaultStatus();
        if (ours < total) OpenDefaultApps();
        return (ours, total, ours < total);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string? extra, System.Text.StringBuilder? output, ref uint length);

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    static void Notify() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
}
