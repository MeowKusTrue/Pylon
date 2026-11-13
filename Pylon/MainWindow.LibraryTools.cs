using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Pylon;

public partial class MainWindow
{
    readonly QueueHistory _queueHistory = new();
    void RememberQueueEdit(params QueueSplice[] edits) => _queueHistory.Push(new(edits, _index, Math.Max(0, _mp.Time)));

    public void UndoQueue()
    {
        var current = CurrentPath;
        var selected = _index;
        if (_queueHistory.Undo(_queue) is not { } edit) { Osd(L.T("Nothing to undo.")); return; }
        var currentIndex = QueueHistory.RestoreIndex(selected, edit);
        if (currentIndex < 0 && edit.Selected >= 0 && edit.Selected < _queue.Count && _queue[edit.Selected] == current) currentIndex = edit.Selected;
        if (currentIndex >= _queue.Count || currentIndex >= 0 && _queue[currentIndex] != current) currentIndex = -1;
        BuildShuffle();
        if (currentIndex >= 0) _index = currentIndex;
        else if (_queue.Count > 0) PlayAt(Math.Clamp(edit.Selected, 0, _queue.Count - 1), startAt: edit.Position, autoplay: false);
        else { Stop(); ShowIdle(); }
        Sidebar.Render(); SaveQueueSession();
        Osd(L.T("Queue change undone."));
    }

    public async void ImportPlaylist()
    {
        var picker = new OpenFileDialog { Filter = "Playlists|*.m3u8;*.m3u;*.xspf", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var lists = await Task.Run(() => picker.FileNames.Select(f => new Playlist { Name = Path.GetFileNameWithoutExtension(f), Items = PlaylistFiles.Read(f) }).ToArray());
            var missing = await Task.Run(() => lists.Sum(l => l.Items.Count(p => !p.Contains("://") && !File.Exists(p))));
            foreach (var list in lists) if (list.Items.Count > 0) Library.Data.Playlists.Add(list);
            Library.Touch(); ToggleSidebar(LibraryPanel.Section.Playlists);
            Osd(L.F("Playlists imported. Missing files: {0}", missing), 6);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        { MessageBox.Show(this, e.Message, L.T("Import playlist")); }
    }

    public async void ExportPlaylist(IEnumerable<string> items)
    {
        var picker = new SaveFileDialog { Filter = "M3U8|*.m3u8|XSPF|*.xspf", FileName = "playlist.m3u8", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        var snapshot = items.ToArray();
        try { await Task.Run(() => PlaylistFiles.Write(picker.FileName, snapshot)); Osd(L.T("Playlist exported with relative paths.")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(this, e.Message, L.T("Export playlist")); }
    }

    public async void RelocateLibrary()
    {
        var oldRoot = DialogWindow.AskName(this, "Find moved files", "Previous folder", "Enter the old folder path, including a disconnected drive.", @"D:\Music",
            Library.Data.MusicFolders.FirstOrDefault() ?? Library.Data.VideoFolders.FirstOrDefault() ?? "");
        if (oldRoot == null) return;
        var folder = new OpenFolderDialog { Title = L.T("Choose the new folder") };
        if (folder.ShowDialog(this) != true) return;
        var paths = LibraryRelocation.KnownFiles(Library.Data).Concat(_queue).ToArray();
        Dictionary<string, string>? map = null;
        var job = BackgroundJobs.Start(L.T("Find moved files"), async (_, cancel) =>
        {
            map = await Task.Run(() => LibraryRelocation.Find(paths, oldRoot, folder.FolderName, cancel), cancel);
            return null;
        });
        await job.Completion;
        if (job.State != JobState.Completed || map == null) { Osd(L.T("Couldn't scan the new folder.")); return; }
        if (map.Count == 0) { Osd(L.T("No moved files found. Nothing was changed.")); return; }
        if (!DialogWindow.ConfirmRelocation(this, map)) return;
        try
        {
            var verified = await Task.Run(() => LibraryRelocation.Find(map.Keys, oldRoot, folder.FolderName, CancellationToken.None));
            if (verified.Count != map.Count) throw new IOException(L.T("Files changed during preview. Scan the folder again."));
            var replacement = LibraryRelocation.Apply(Library.Data, map, oldRoot, folder.FolderName);
            Library.Save(); // preserve the previous document before replacing paths
            Library.ReplaceData(replacement);
            for (var i = 0; i < _queue.Count; i++) _queue[i] = map.GetValueOrDefault(_queue[i]) ?? _queue[i];
            _queueHistory.Clear(); SaveQueueSession(); Library.Save();
            Osd(L.F("Relinked files: {0}", map.Count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(this, e.Message, L.T("Find moved files")); }
    }
}
