using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pylon;

/// <summary>
/// A second subtitle at the top of the picture (a translation next to the original, for learning a language): a text
/// file beside the video or chosen by hand, or a track inside the video taken out with ffmpeg. libVLC 3 shows one
/// subtitle only, so Pylon draws this one itself, following the playback clock.
/// </summary>
public partial class MainWindow
{
    List<SubText.Cue>? _second;
    string? _secondName;
    DispatcherTimer? _secondTimer;
    int _secondAt;

    List<object> SecondSubtitleItems()
    {
        var items = new List<object> { Item("Off", null, null, () => SetSecondSubtitle(null, null), check: _second == null) };
        if (CurrentPath is not { } video || video.Contains("://") || _isAudio) return items;
        foreach (var f in SubText.Beside(video))
        {
            var file = f;
            items.Add(Item(Path.GetFileName(file), null, null, () => SetSecondSubtitle(SubText.Load(file), Path.GetFileName(file)), check: _secondName == Path.GetFileName(file)));
        }
        // tracks inside the file: subtitle stream N for ffmpeg is the N-th subtitle track libVLC lists
        var tracks = _mp.SpuDescription.Where(t => t.Id >= 0).ToList();
        for (var n = 0; n < tracks.Count; n++)
        {
            var (index, name) = (n, tracks[n].Name ?? L.F("Track {0}", n + 1));
            items.Add(Item(name, null, null, () => SecondFromTrack(video, index, name), check: _secondName == name));
        }
        items.Add(new System.Windows.Controls.Separator());
        items.Add(Item("Choose File…", "file-duotone", null, () =>
        {
            var ofd = new OpenFileDialog { Filter = L.T("Subtitles") + "|*.srt;*.vtt;*.ass;*.ssa", InitialDirectory = Path.GetDirectoryName(video) };
            if (ofd.ShowDialog(this) == true) SetSecondSubtitle(SubText.Load(ofd.FileName), Path.GetFileName(ofd.FileName));
        }));
        return items;
    }

    void SetSecondSubtitle(List<SubText.Cue>? cues, string? name)
    {
        if (cues is { Count: 0 }) { Osd(L.T("No text subtitles in that file"), 3); return; }
        (_second, _secondName, _secondAt) = (cues, name, 0);
        SecondSub.Visibility = Visibility.Collapsed;
        if (cues == null) { _secondTimer?.Stop(); return; }
        _secondTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => ShowSecondSubtitle(), Dispatcher);
        _secondTimer.Start();
        Osd(L.F("Second subtitle: {0}", name ?? ""), 2);
    }

    void ShowSecondSubtitle()
    {
        if (_second == null || Motion.Asleep) return;
        var t = PlaybackClock() - Settings.Num("subs.delay"); // the subtitle delay moves both subtitles
        // the cue at t: usually the same or the next one, so walk from the last one instead of searching every time
        if (_secondAt >= _second.Count || _second[_secondAt].Start > t) _secondAt = Math.Max(0, _second.FindLastIndex(c => c.Start <= t));
        while (_secondAt + 1 < _second.Count && _second[_secondAt + 1].Start <= t) _secondAt++;
        var cue = _second.Count > 0 && _second[_secondAt].Start <= t && t < _second[_secondAt].End ? _second[_secondAt] : null;
        SecondSubText.FontSize = Math.Clamp(Video.ActualHeight / 28, 14, 40);
        if (cue == null) { SecondSub.Visibility = Visibility.Collapsed; return; }
        if (SecondSubText.Text != cue.Text) SecondSubText.Text = cue.Text;
        SecondSub.Visibility = Visibility.Visible;
    }

    CancellationTokenSource? _whisper;

    /// <summary>Subtitles from the video's speech (whisper.cpp) in the background; loaded when done if the video still plays.</summary>
    async void SubtitlesFromSpeech()
    {
        if (_whisper != null) { _whisper.Cancel(); _whisper = null; Osd(L.T("Stopped"), 2); return; }
        if (CurrentPath is not { } video || video.Contains("://")) return;
        if (DialogWindow.WhisperOptions(this) is not { } o) return;
        _whisper = new CancellationTokenSource();
        var cancel = _whisper.Token;
        var last = DateTime.MinValue;
        var progress = new Progress<(string Stage, double P)>(s =>
        {
            if ((DateTime.Now - last).TotalSeconds < 1 && s.P < 1) return;
            last = DateTime.Now;
            Osd($"{s.Stage} {(int)(s.P * 100)}%", 3);
        });
        var srt = await Whisper.RunAsync(video, _mp.Length / 1000.0, o, progress, cancel);
        _whisper = null;
        if (cancel.IsCancellationRequested) return;
        if (srt == null) { Osd(L.F("Subtitles from speech failed: {0}", Whisper.Error ?? "?"), 6); return; }
        var list = Library.Data.Subtitles.TryGetValue(video, out var l) ? l : Library.Data.Subtitles[video] = [];
        if (!list.Contains(srt, StringComparer.OrdinalIgnoreCase)) list.Add(srt);
        Library.Touch();
        if (CurrentPath == video) _mp.AddSlave(LibVLCSharp.Shared.MediaSlaveType.Subtitle, new Uri(srt).AbsoluteUri, true);
        Osd(L.F("Subtitles ready: {0}", Path.GetFileName(srt)), 5);
    }

    /// <summary>A subtitle track inside the video as text, through ffmpeg (offered for download the first time).</summary>
    async void SecondFromTrack(string video, int index, string name)
    {
        if (!Ffmpeg.Available)
        {
            if (!DialogWindow.OfferTool(this, "ffmpeg", "Second subtitle from the video",
                    "Taking a subtitle out of the video needs ffmpeg, a free open-source tool. Pylon can download it now (about 200 MB from github.com/BtbN/FFmpeg-Builds).")) return;
            Osd(L.T("Downloading ffmpeg…"), 60);
            if (!await Ffmpeg.InstallAsync(new Progress<double>(p => Osd(L.F("ffmpeg {0}%", (int)(p * 100)), 60)))) { Osd(L.T("Couldn't download ffmpeg"), 3); return; }
        }
        Osd(L.T("Reading the subtitle track…"), 30);
        var text = await Task.Run(() =>
        {
            var psi = new ProcessStartInfo(Ffmpeg.Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in new[] { "-v", "error", "-i", video, "-map", $"0:s:{index}", "-f", "srt", "-" }) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi)!;
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                return p.WaitForExit(120_000) ? stdout.Result : null;
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
        });
        if (CurrentPath != video) return;
        var cues = text == null ? [] : SubText.Parse(text, ".srt");
        // picture subtitles (Blu-ray PGS, DVD VobSub) have no text to show
        if (cues.Count == 0) { Osd(L.T("This track is pictures, not text: it can't be the second subtitle"), 4); return; }
        SetSecondSubtitle(cues, name);
    }
}
