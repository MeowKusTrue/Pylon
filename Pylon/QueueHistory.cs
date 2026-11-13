namespace Pylon;

public record QueueSplice(int Index, string[] Removed, string[] Added);
public record QueueEdit(QueueSplice[] Splices, int Selected, long Position);

public sealed class QueueHistory
{
    readonly List<QueueEdit> _edits = [];
    public int Count => _edits.Count;
    public void Push(QueueEdit edit)
    {
        _edits.Add(edit);
        if (_edits.Count > 20) _edits.RemoveAt(0);
    }
    public void Clear() => _edits.Clear();
    public static int RestoreIndex(int index, QueueEdit edit)
    {
        foreach (var s in edit.Splices.Reverse())
        {
            if (index < 0) break;
            if (index >= s.Index + s.Added.Length) index += s.Removed.Length - s.Added.Length;
            else if (index >= s.Index) index = -1;
        }
        return index;
    }
    public QueueEdit? Undo(List<string> queue)
    {
        if (_edits.Count == 0) return null;
        var edit = _edits[^1];
        // Validate the whole transaction before changing the real queue.
        var copy = queue.ToList();
        foreach (var s in edit.Splices.Reverse())
        {
            if (s.Index < 0 || s.Index + s.Added.Length > copy.Count || !copy.Skip(s.Index).Take(s.Added.Length).SequenceEqual(s.Added))
            { _edits.Clear(); return null; }
            copy.RemoveRange(s.Index, s.Added.Length);
            copy.InsertRange(s.Index, s.Removed);
        }
        _edits.RemoveAt(_edits.Count - 1);
        queue.Clear(); queue.AddRange(copy);
        return edit;
    }
}
