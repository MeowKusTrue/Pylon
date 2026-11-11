using System.Windows;

namespace Pylon;

public partial class MainWindow
{
    AudioOutputWatch? _outputWatch;
    bool _recoveringOutput;
    (string[] Names, string Path, long Position, long Request)? _deferredOutput;

    void WatchAudioOutputs()
    {
        if (SnapMode || Perf.Enabled) return;
        try { _outputWatch = new AudioOutputWatch(Dispatcher, OutputsChanged); }
        catch (Exception e) { ErrorLog.Note("audio watcher", e.GetType().Name); }
    }

    void OutputsChanged(string[] names, bool defaultChanged)
    {
        if (!IsLoaded || _recoveringOutput || CurrentPath == null || !Settings.Bool("audio.enable")) return;
        if (_deferredOutput is { } pending && pending.Path == CurrentPath && pending.Request == _playRequest)
        { _deferredOutput = (names, pending.Path, pending.Position, pending.Request); return; }
        if (AudioOutputWatch.IsMissing(Settings.Str("audio.device"), names)
            || defaultChanged && Settings.Str("audio.device") == "System default" && (_mp.IsPlaying || _openingMedia))
            RecoverAudioOutput(names);
    }

    async void RecoverAudioOutput(string[] names, long? position = null, bool? resume = null)
    {
        if (_recoveringOutput || CurrentPath is not { } path) return;
        _deferredOutput = null;
        _recoveringOutput = true;
        var at = Math.Max(0, position ?? _mp.Time);
        var play = resume ?? _mp.IsPlaying;
        _openingMedia = true; // native Stop can wait for the removed device: keep that wait off the UI
        Stop();
        var request = _playRequest;
        try
        {
            await _pendingStop;
            if (!IsLoaded || request != _playRequest || path != CurrentPath) return;
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                _deferredOutput = (names, path, at, request);
                ErrorLog.Note("audio devices", "Playback stopped; output selection waits for the next Play action.");
                return;
            }
            Osd(L.T("Audio output changed or disconnected. Playback is stopped; choose an output to continue."), 10);
            if (DialogWindow.RecoverAudioOutput(this, names) is not { } selected || request != _playRequest) return;
            Settings.Set("audio.device", selected);
            CreateEngine(resume: false);
            PlayAt(_index, startAt: at, autoplay: play);
        }
        finally { _recoveringOutput = false; }
    }

    bool RecoverDeferredOutput()
    {
        var pending = _deferredOutput;
        _deferredOutput = null;
        if (pending is not { } saved || saved.Path != CurrentPath || saved.Request != _playRequest) return false;
        RecoverAudioOutput(saved.Names, saved.Position, resume: true);
        return true;
    }
}
