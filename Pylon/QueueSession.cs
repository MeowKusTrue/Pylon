using System.IO;
using System.Text.Json;

namespace Pylon;

public record QueueState(string[] Items, int Index, long Position);

public static class QueueSession
{
    static string FilePath => Path.Combine(Library.Dir, "queue.json");
    public static QueueState? Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<QueueState>(File.ReadAllText(FilePath));
            return saved is { Items.Length: > 0 } && saved.Items.All(s => !string.IsNullOrWhiteSpace(s))
                ? saved with { Index = Math.Clamp(saved.Index, 0, saved.Items.Length - 1), Position = Math.Max(0, saved.Position) } : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(IReadOnlyList<string> items, int index, long position)
    {
        var temporary = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Library.Dir);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new QueueState([.. items], index, Math.Max(0, position))));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorLog.Note("queue save", e.GetType().Name); }
        finally { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
