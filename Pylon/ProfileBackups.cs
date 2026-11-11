using System.IO;
using System.Text.Json;

namespace Pylon;

/// <summary>Three previous valid snapshots per document; originals survive a failed restore.</summary>
public static class ProfileBackups
{
    static readonly object Gate = new();
    static string Folder => Path.Combine(Library.Dir, "backups");

    static void Validate(string document, byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (document == "settings.json") Settings.ParseProfile(text);
        else if (document == "library.json") Library.ParseProfile(text);
        else throw new ArgumentException("Unknown profile document.");
    }

    public static void BeforeReplace(string path, bool force = false)
    {
        lock (Gate)
        try
        {
            if (!File.Exists(path)) return;
            var name = Path.GetFileNameWithoutExtension(path);
            var stamp = DateTime.UtcNow.ToString(force ? "yyyyMMdd-HHmmss-fffffff" : "yyyyMMdd");
            var backup = Path.Combine(Folder, $"{name}-{stamp}-v{Updater.Current.ToString(3)}.json");
            if (File.Exists(backup)) return;
            var bytes = File.ReadAllBytes(path);
            Validate(Path.GetFileName(path), bytes);
            Directory.CreateDirectory(Folder);
            var temporary = backup + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, backup); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            foreach (var old in Directory.EnumerateFiles(Folder, name + "-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(3)) File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { ErrorLog.Note("profile backup", e.GetType().Name); }
    }

    public static string? Latest(string document)
    {
        try
        {
            if (Directory.Exists(Folder))
                foreach (var file in Directory.EnumerateFiles(Folder, Path.GetFileNameWithoutExtension(document) + "-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
                    try { Validate(document, File.ReadAllBytes(file)); return file; }
                    catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Restore(string document, string backup)
    {
        if (document is not ("settings.json" or "library.json")) throw new ArgumentException("Unknown profile document.");
        lock (Gate)
        {
            var bytes = File.ReadAllBytes(backup);
            Validate(document, bytes);
            var path = Path.Combine(Library.Dir, document);
            if (File.Exists(path)) File.Copy(path, path + ".unreadable-" + Guid.NewGuid().ToString("N"));
            var temporary = path + ".restore-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
