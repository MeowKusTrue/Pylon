using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;

namespace Pylon;

public sealed class PlaybackPipeline
{
    public string Decoder { get; private set; } = "Not reported by the engine";
    public string Renderer { get; private set; } = "Not reported by the engine";
    public string Fallback { get; set; } = "";
    public void Reset() { Decoder = Renderer = "Not reported by the engine"; Fallback = ""; }
    public void Observe(string module, string message)
    {
        if (message.Contains("Using D3D11VA", StringComparison.OrdinalIgnoreCase)) Decoder = "D3D11VA";
        else if (message.Contains("Using DXVA2", StringComparison.OrdinalIgnoreCase)) Decoder = "DXVA2";
        if (message.Contains("using vout display module", StringComparison.OrdinalIgnoreCase))
            Renderer = message.Contains("direct3d11", StringComparison.OrdinalIgnoreCase) ? "Direct3D 11"
                : message.Contains("direct3d9", StringComparison.OrdinalIgnoreCase) ? "Direct3D 9" : "Other video output";
    }
}

public partial class MainWindow
{
    readonly PlaybackPipeline _pipeline = new();
    public string PlaybackPipelineText()
    {
        var video = _mp.Media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        var codec = video is { Codec: > 0 } v ? $"{FourCC(v.Codec)} · {v.Data.Video.Width}×{v.Data.Video.Height}" : L.T("No video track");
        var decoder = _mp.OnOwnEngine ? _mp.OwnHardwareDecoding ? "D3D11VA" : L.T("Software decoding") : L.T(_pipeline.Decoder);
        return $"{L.T("Engine")}: {(_mp.OnOwnEngine ? "Pylon" : "libVLC")}\n{codec}\n"
            + $"{L.T("Decoder")}: {decoder}\n{L.T("Video output")}: {(_mp.OnOwnEngine ? "Direct3D 11" : L.T(_pipeline.Renderer))}\n"
            + $"{L.T("Audio output")}: {AudioChain.Output} · {Settings.Str("audio.device")}\n"
            + $"{L.T("HDR preference")}: {Settings.Str("video.hdr")}\n"
            + L.T("The HDR preference is not proof of active HDR output. Display color spaces are listed below.") + "\n"
            + $"{L.T("Engine selection")}: {(_pipeline.Fallback.Length > 0 ? _pipeline.Fallback : Settings.Bool("playback.ownengine") ? L.T("Automatic compatibility selection") : L.T("Own engine is disabled"))}\n"
            + $"{L.T("Economical effects")}: {PowerPolicy.Economy || Motion.Lite}\n{L.T("Safe mode")}: {StartupOptions.SafeMode}\n"
            + $"{L.T("Portable mode")}: {StartupOptions.Portable}";
    }
    public void ShowPlaybackPipeline() => DialogWindow.PlaybackPipeline(this);
}

public partial class DialogWindow
{
    public static void PlaybackPipeline(MainWindow owner)
    {
        var report = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 390,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = R("BgB2"), Foreground = R("FgA1"), Padding = new Thickness(12) };
        var refresh = new Button { Content = L.T("Refresh"), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var dialog = new DialogWindow(owner, "Playback diagnostics", Stack(report, refresh)) { Width = 760 };
        dialog.SaveBtn.Content = L.T("Copy report"); dialog.CancelBtn.Content = L.T("Close");
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        async void Update()
        {
            var current = owner.PlaybackPipelineText();
            var displays = await InterfaceRendering.ReportAsync(owner);
            if (!closed) report.Text = current + "\n\n" + displays;
        }
        dialog.Loaded += (_, _) => Update(); refresh.Click += (_, _) => Update();
        dialog._work = () => { try { Clipboard.SetText(report.Text); } catch (System.Runtime.InteropServices.COMException) { } return Task.CompletedTask; };
        dialog.Run(() => report.Text.Length > 0);
    }
}
