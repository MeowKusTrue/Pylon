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
        Notify();
    }

    public static void Unregister()
    {
        using var hkcu = Registry.CurrentUser;
        foreach (var key in new[] { $@"{Classes}\Pylon.Video", $@"{Classes}\Pylon.Audio", $@"{Classes}\Applications\Pylon.exe", $@"{Classes}\Directory\shell\Pylon.Play", @"Software\Pylon" }
                     .Concat(Formats().Select(f => $@"{Classes}\{f.Id}")))
            hkcu.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using (var reg = hkcu.OpenSubKey(@"Software\RegisteredApplications", writable: true)) reg?.DeleteValue("Pylon", throwOnMissingValue: false);
        foreach (var (ext, id) in Extensions())
            using (var k = hkcu.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", writable: true))
            {
                k?.DeleteValue(id, throwOnMissingValue: false);
                k?.DeleteValue("Pylon.Audio", throwOnMissingValue: false); // entries written by 1.0-1.2
                k?.DeleteValue("Pylon.Video", throwOnMissingValue: false);
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

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    static void Notify() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
}
