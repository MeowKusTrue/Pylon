using System.IO;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pylon;

/// <summary>
/// Windows' media session (System Media Transport Controls): the track, artist and cover in the volume flyout, on the
/// lock screen and in the Windows 11 media widget, plus the hardware media keys and the flyout's buttons and scrubber.
/// </summary>
public sealed class MediaSession
{
    readonly SystemMediaTransportControls _smtc;
    MediaPlaybackStatus _status = MediaPlaybackStatus.Closed;

    /// <summary>"Play", "Pause", "Next", "Previous" or "Stop", raised on a background thread.</summary>
    public event Action<string>? Command;

    /// <summary>The user dragged the scrubber in the Windows flyout (background thread).</summary>
    public event Action<TimeSpan>? Seek;

    public MediaSession(IntPtr hwnd)
    {
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = _smtc.IsStopEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += (_, e) => Command?.Invoke(e.Button.ToString());
        _smtc.PlaybackPositionChangeRequested += (_, e) => Seek?.Invoke(e.RequestedPlaybackPosition);
    }

    public async void SetTrack(bool audio, string title, string? artist, string? album, string? artworkUrl)
    {
        var u = _smtc.DisplayUpdater;
        u.ClearAll();
        u.Type = audio ? MediaPlaybackType.Music : MediaPlaybackType.Video;
        if (audio)
        {
            u.MusicProperties.Title = title;
            u.MusicProperties.Artist = artist ?? "";
            u.MusicProperties.AlbumTitle = album ?? "";
        }
        else
        {
            u.VideoProperties.Title = title;
            u.VideoProperties.Subtitle = artist ?? "";
        }
        try
        {
            // libVLC hands artwork out as file:///… (its art cache or the file next to the track)
            if (artworkUrl != null && Uri.TryCreate(artworkUrl, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
                u.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(uri.LocalPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { } // no cover then
        u.Update();
    }

    public void SetState(bool playing, bool active)
    {
        var s = !active ? MediaPlaybackStatus.Stopped : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (s == _status) return;
        _status = s;
        _smtc.PlaybackStatus = s;
    }

    public void SetTimeline(TimeSpan position, TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero, EndTime = length, MinSeekTime = TimeSpan.Zero, MaxSeekTime = length, Position = position,
        });
    }

    public void Clear()
    {
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        SetState(false, false);
    }
}
