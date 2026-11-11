namespace Pylon;

public partial class MainWindow
{
    long _queueResume = -1;

    void RestoreQueueSession()
    {
        if (QueueSession.Load() is not { } saved) return;
        _queue.Clear();
        _queue.AddRange(saved.Items);
        _index = saved.Index;
        _queueResume = saved.Position;
        BuildShuffle();
        Sidebar.Render();
    }

    internal void SaveQueueSession()
    {
        var position = _deferredOutput is { } pending && pending.Path == CurrentPath && pending.Request == _playRequest
            ? pending.Position : _mp.Media == null && _queueResume >= 0 ? _queueResume : _mp.Time;
        QueueSession.Save(_queue, _index, position);
    }
}
