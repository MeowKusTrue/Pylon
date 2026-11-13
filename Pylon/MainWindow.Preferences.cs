using System.IO;
using LibVLCSharp.Shared;

namespace Pylon;

public partial class MainWindow
{
    void PowerEffectsChanged()
    {
        Scene.Active = false; Scene.Idle = false;
        ApplyLayout(); LyricsPane.Restyle();
        Refresh();
    }
    double _fileAudioDelay, _fileSubtitleDelay;
    bool _restoringPreferences;
    TrackCandidate[] TrackCandidates(bool audio) => (audio ? _mp.AudioTrackDescription : _mp.SpuDescription).Where(t => t.Id >= 0).Select(t =>
    {
        var meta = _mp.Media?.Tracks.FirstOrDefault(m => m.Id == t.Id && m.TrackType == (audio ? TrackType.Audio : TrackType.Text));
        return new TrackCandidate(t.Id, meta?.Description is { Length: > 0 } description ? description : t.Name ?? "", meta?.Language ?? "");
    }).ToArray();

    TrackPreference TrackChoiceNow(bool audio)
    {
        var id = audio ? _mp.AudioTrack : _mp.Spu;
        var candidate = TrackCandidates(audio).FirstOrDefault(t => t.Id == id);
        return new(candidate?.Name ?? "", candidate?.Language ?? "", id < 0);
    }

    void RememberPlaybackChoices()
    {
        if (_restoringPreferences || _openingMedia || StartupOptions.SafeMode || CurrentPath is not { } path || _mp.Media == null || !Settings.Bool("privacy.position")) return;
        var preference = new PlaybackPreference(TrackChoiceNow(true), TrackChoiceNow(false), _mp.Rate, _fileAudioDelay, _fileSubtitleDelay);
        if (!PlaybackPreferences.Valid(preference)) return;
        Library.Data.Playback[path] = preference;
        Library.Touch();
    }

    void RestorePlaybackChoices()
    {
        if (CurrentPath is not { } path || StartupOptions.SafeMode) return;
        var saved = Library.Data.Playback.GetValueOrDefault(path);
        var series = PlaybackPreferences.Folder(path) is { } folder ? Library.Data.SeriesTracks.GetValueOrDefault(folder) : null;
        _restoringPreferences = true;
        try
        {
            if (PlaybackPreferences.Match(saved?.Audio ?? series?.Audio, TrackCandidates(true)) is { } audio) _mp.SetAudioTrack(audio);
            if (PlaybackPreferences.Match(saved?.Subtitles ?? series?.Subtitles, TrackCandidates(false)) is { } sub) _mp.SetSpu(sub);
            _fileAudioDelay = saved?.AudioDelay ?? Settings.Num("audio.delay");
            _fileSubtitleDelay = saved?.SubtitleDelay ?? Settings.Num("subs.delay");
            _mp.SetAudioDelay((long)(_fileAudioDelay * 1_000_000));
            _mp.SetSpuDelay((long)(_fileSubtitleDelay * 1_000_000));
            if (saved != null) _mp.SetRate((float)saved.Speed);
        }
        finally { _restoringPreferences = false; }
    }

    void ChooseAudioTrack(int id) { _mp.SetAudioTrack(id); RememberPlaybackChoices(); }
    void ChooseSubtitleTrack(int id) { _mp.SetSpu(id); RememberPlaybackChoices(); }

    public void RememberSeriesTracks()
    {
        if (_openingMedia || _mp.Media == null || CurrentPath is not { } path || PlaybackPreferences.Folder(path) is not { } folder) return;
        Library.Data.SeriesTracks[folder] = new(TrackChoiceNow(true), TrackChoiceNow(false));
        Library.Touch();
        Osd(L.T("Track choices saved for this folder."));
    }
}
