using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Pylon;

/// <summary>
/// Watch Together over the internet: the host's Pylon sends what it plays to a small relay server (relay/party-relay.py on
/// any VPS), and friends anywhere open the room's link. Two loops: the state (title, play / pause, position) every second,
/// answered with the guests' commands (pause for everyone); and the stream, which Pylon reads from its own local cast
/// server (an MPEG-TS copy of playback, nothing re-encoded) and pushes up as one long chunked upload. Guests never need the
/// file. A room exists while its host sends; its key in the host's requests keeps others from taking it over.
/// </summary>
public sealed class PartyRelay : IDisposable
{
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly CancellationTokenSource _stop = new();
    readonly string _host;
    readonly Func<object> _state;
    readonly Action<string> _command;
    readonly Func<string?> _stream;

    /// <summary>The room's page: what friends open (also the address Pylon joins with).</summary>
    public string RoomUrl { get; }

    /// <summary>The relay answered the last state update.</summary>
    public bool Connected { get; private set; }

    /// <param name="server">The relay's address, e.g. https://party.example.com</param>
    /// <param name="state">What guests see (the phone remote's state object); called on the UI thread.</param>
    /// <param name="command">A guest's command ("toggle"); run on the UI thread.</param>
    /// <param name="stream">The local cast stream's address, or null while there is none.</param>
    public PartyRelay(string server, Func<object> state, Action<string> command, Func<string?> stream)
    {
        var room = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        RoomUrl = $"{server.Trim().TrimEnd('/')}/r/{room}/";
        _host = $"{RoomUrl}host/";
        (_state, _command, _stream) = (state, command, stream);
        _ = StateLoop(key);
        _ = StreamLoop(key);
    }

    async Task StateLoop(string key)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var body = new StringContent(JsonSerializer.Serialize(_state()), Encoding.UTF8, "application/json");
                using var res = await _http.PostAsync($"{_host}state?key={key}", body, _stop.Token).WaitAsync(TimeSpan.FromSeconds(8));
                Connected = res.IsSuccessStatusCode;
                if (Connected)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(_stop.Token));
                    if (doc.RootElement.TryGetProperty("cmds", out var cmds))
                        foreach (var c in cmds.EnumerateArray()) if (c.GetString() is { } cmd) _command(cmd);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or InvalidOperationException) { Connected = false; }
            try { await Task.Delay(1000, _stop.Token); } catch (TaskCanceledException) { return; }
        }
    }

    async Task StreamLoop(string key)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_stream() is { } local)
                {
                    using var source = await _http.GetAsync(local, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
                    await using var bytes = await source.Content.ReadAsStreamAsync(_stop.Token);
                    using var push = new HttpRequestMessage(HttpMethod.Post, $"{_host}stream?key={key}") { Content = new StreamContent(bytes, 64 * 1024) };
                    push.Headers.TransferEncodingChunked = true; // endless: as long as playback goes on
                    using var _ = await _http.SendAsync(push, _stop.Token);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { }
            try { await Task.Delay(2000, _stop.Token); } catch (TaskCanceledException) { return; } // reconnect (a new track, a dropped line)
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}

public partial class DialogWindow
{
    /// <summary>
    /// Watch Together, hosting: the address friends join with (and its QR code). Through a relay it works from anywhere and
    /// Discord shows a "Watch together" button; without one, friends need the same network or a shared VPN. True = stop.
    /// </summary>
    public static bool Party(Window owner, string url, bool relayed)
    {
        var qr = QrBox(url);
        var info = Stack(
            new TextBlock
            {
                Text = L.T(relayed
                    ? "Friends open this link (or press \"Watch together\" in your Discord profile): Pylon joins and plays your picture and sound, no file needed. Pausing on their side pauses everyone."
                    : "Friends paste this address in Pylon → ⋯ → Watch Together → Join a friend. Same Wi-Fi, or a shared VPN such as Radmin, ZeroTier or Tailscale. Those without the file watch your stream. For friends anywhere, set a Watch Together server in Settings → Privacy."),
                TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = R("FgA2"), LineHeight = 21,
            },
            Spacer(14),
            Address(url));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(qr, Dock.Left);
        row.Children.Add(qr);
        row.Children.Add(info);
        var dlg = new DialogWindow(owner, "Watch Together", Stack(Section(L.T("Watch Together"), L.T("You lead: play, pause and seeking follow you")), row)) { Width = 720 };
        dlg.CancelBtn.Content = L.T("Keep it on");
        dlg.SaveBtn.Content = L.T("Stop");
        return dlg.Run(() => true);
    }
}
