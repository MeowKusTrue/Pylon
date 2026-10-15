using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using QRCoder;

namespace Pylon;

/// <summary>
/// "Stream to TV / phone": libVLC duplicates playback into an MPEG-TS stream over HTTP (no re-encoding), and this tiny
/// server hands out a landing page on the same network with an "Open in VLC" link and the address to type into a TV app.
/// A plain TcpListener needs no URL reservation (HttpListener on a LAN address would need admin rights).
/// </summary>
public sealed class CastServer : IDisposable
{
    readonly TcpListener _page;
    readonly CancellationTokenSource _stop = new();
    public string Host { get; }
    public int PagePort { get; }
    public int StreamPort { get; }
    public string Title { get; set; } = "Pylon";

    public string StreamUrl => $"http://{Host}:{StreamPort}/stream.ts";
    public string PageUrl => $"http://{Host}:{PagePort}/";

    /// <summary>libVLC's stream output for the duplicate branch.</summary>
    public string SoutBranch => $"http{{mux=ts,dst={(Loopback ? "127.0.0.1" : "")}:{StreamPort}/stream.ts}}";

    // test runs stay on this machine: listening on the network would raise a Windows Firewall prompt
    static readonly bool Loopback = Environment.GetEnvironmentVariable("PYLON_CAST_LOOPBACK") != null;

    public CastServer()
    {
        Host = Loopback ? "127.0.0.1" : LanAddress() ?? "127.0.0.1";
        _page = new TcpListener(Loopback ? IPAddress.Loopback : IPAddress.Any, 0);
        _page.Start();
        PagePort = ((IPEndPoint)_page.LocalEndpoint).Port;
        StreamPort = FreePort();
        _ = Serve();
    }

    static int FreePort()
    {
        var l = new TcpListener(Loopback ? IPAddress.Loopback : IPAddress.Any, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>The address other devices on this network reach us at: an up, non-virtual adapter with a gateway.</summary>
    public static string? LanAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase))
        .Select(n => n.GetIPProperties())
        .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
        .SelectMany(p => p.UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();

    async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _page.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buf = new byte[4096];
                    try { await stream.ReadAsync(buf, _stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch { return; } // the request itself doesn't matter: one page
                    var html = Encoding.UTF8.GetBytes(Page());
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    try { await stream.WriteAsync(head); await stream.WriteAsync(html); } catch (System.IO.IOException) { }
                }
            });
        }
    }

    string Page()
    {
        var title = WebUtility.HtmlEncode(Title);
        var url = WebUtility.HtmlEncode(StreamUrl);
        var ru = L.Ru;
        return $$"""
            <!doctype html><html lang="{{(ru ? "ru" : "en")}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Pylon — {{title}}</title>
            <style>
              body{margin:0;min-height:100vh;display:grid;place-items:center;background:#111;color:#ddd;font:16px/1.5 system-ui,sans-serif}
              main{max-width:420px;padding:24px}h1{font-size:20px;color:#fff;margin:0 0 4px}p{color:#999}
              a.btn{display:block;text-align:center;background:#d37918;color:#111;font-weight:600;padding:14px;border-radius:12px;text-decoration:none;margin:20px 0 12px}
              code{display:block;background:#1d1d1d;padding:12px;border-radius:10px;word-break:break-all;color:#eee;user-select:all}
            </style></head><body><main>
            <h1>{{title}}</h1><p>{{(ru ? "Трансляция из Pylon" : "Streaming from Pylon")}}</p>
            <a class="btn" href="vlc://{{url}}">{{(ru ? "Открыть в VLC" : "Open in VLC")}}</a>
            <p>{{(ru ? "Или вставьте этот адрес в VLC, Kodi или плеер телевизора («Сетевой поток»):" : "Or paste this address into VLC, Kodi or your TV's player (\"Network stream\"):")}}</p>
            <code>{{url}}</code>
            </main></body></html>
            """;
    }

    /// <summary>The landing page address as a QR code, drawn in the current theme colors.</summary>
    public ImageSource QrCode(Brush dark, Brush light) => Qr(PageUrl, dark, light);

    public static ImageSource Qr(string url, Brush dark, Brush light)
    {
        using var data = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (var y = 0; y < m.Count; y++)
            for (var x = 0; x < m[y].Length; x++)
                if (m[y][x]) g.Children.Add(new RectangleGeometry(new System.Windows.Rect(x, y, 1, 1)));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new System.Windows.Rect(0, 0, m.Count, m.Count))));
        drawing.Children.Add(new GeometryDrawing(dark, null, g));
        var img = new DrawingImage(drawing);
        img.Freeze();
        return img;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _page.Stop();
    }
}
