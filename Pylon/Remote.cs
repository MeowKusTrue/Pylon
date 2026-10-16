using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Phone remote: a page on the local network (opened from a QR code) with play / pause, previous / next, ±15 s, volume and
/// what's playing. A random key in the address keeps others on the network out. Same plain TcpListener as CastServer.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    readonly Func<string, int, string?, object> _command;

    public string Url { get; }

    /// <param name="command">Runs a command ("state", "toggle", "seek", "prev", "next", "back", "forward", "volume") with its
    /// number argument and, for a Watch Together guest, their name, on the UI thread; returns the state to send back.</param>
    public RemoteServer(Func<string, int, string?, object> command)
    {
        _command = command;
        var loopback = Environment.GetEnvironmentVariable("PYLON_CAST_LOOPBACK") != null; // tests: no firewall prompt
        _listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, 0);
        _listener.Start();
        Url = $"http://{(loopback ? "127.0.0.1" : CastServer.LanAddress() ?? "127.0.0.1")}:{((IPEndPoint)_listener.LocalEndpoint).Port}/{_key}/";
        _ = Serve();
    }

    async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buf = new byte[2048];
                    int n;
                    try { n = await stream.ReadAsync(buf, _stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception) { return; } // slow or closed: nothing to answer
                    // "GET /<key>/api/volume?v=40 HTTP/1.1"
                    var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ').ElementAtOrDefault(1) ?? "";
                    var (status, type, body) = Answer(path);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    try { await stream.WriteAsync(head); await stream.WriteAsync(body); } catch (System.IO.IOException) { }
                }
            });
        }
    }

    (string Status, string Type, byte[] Body) Answer(string path)
    {
        if (!path.StartsWith($"/{_key}/")) return ("404 Not Found", "text/plain", "Not found"u8.ToArray());
        var rest = path[(_key.Length + 2)..];
        if (!rest.StartsWith("api/")) return ("200 OK", "text/html", Encoding.UTF8.GetBytes(Page()));
        var q = rest[4..].Split('?', 2);
        var query = System.Web.HttpUtility.ParseQueryString(q.Length > 1 ? q[1] : "");
        var arg = int.TryParse(query["v"], out var v) ? v : 0;
        var who = query["n"] is { Length: > 0 } n ? n[..Math.Min(n.Length, 40)] : null;
        var state = System.Windows.Application.Current.Dispatcher.Invoke(() => _command(q[0], arg, who));
        return ("200 OK", "application/json", JsonSerializer.SerializeToUtf8Bytes(state));
    }

    static string Page()
    {
        var ru = L.Ru;
        return $$"""
            <!doctype html><html lang="{{(ru ? "ru" : "en")}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1,user-scalable=no"><meta name="theme-color" content="#111">
            <title>Pylon</title>
            <style>
              *{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
              body{margin:0;min-height:100vh;display:flex;flex-direction:column;justify-content:center;background:#111;color:#eee;font:16px/1.4 system-ui,sans-serif;padding:24px}
              h1{font-size:22px;margin:0;color:#fff;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
              #artist{color:#999;margin:4px 0 20px;min-height:1.4em}
              #bar{height:6px;background:#2a2a2a;border-radius:3px;overflow:hidden}#fill{height:100%;width:0;background:#d37918}
              .t{display:flex;justify-content:space-between;color:#888;font-size:13px;margin-top:6px}
              .row{display:flex;justify-content:space-between;align-items:center;margin:28px 0}
              button{border:0;background:#222;color:#eee;border-radius:50%;width:64px;height:64px;font-size:22px}
              button:active{transform:scale(.92);background:#333}
              #toggle{width:88px;height:88px;background:#d37918;color:#111;font-size:30px}
              input[type=range]{width:100%;accent-color:#d37918}
              .vol{display:flex;gap:12px;align-items:center;color:#999}
            </style></head><body>
            <h1 id="title">Pylon</h1><div id="artist"></div>
            <div id="bar"><div id="fill"></div></div><div class="t"><span id="time">0:00</span><span id="len">0:00</span></div>
            <div class="row">
              <button onclick="go('prev')" aria-label="previous">⏮</button><button onclick="go('back')" aria-label="-15s">↺</button>
              <button id="toggle" onclick="go('toggle')" aria-label="play">▶</button>
              <button onclick="go('forward')" aria-label="+15s">↻</button><button onclick="go('next')" aria-label="next">⏭</button>
            </div>
            <div class="vol">🔈<input id="vol" type="range" min="0" max="100" oninput="go('volume',this.value)">🔊</div>
            <script>
              const $=id=>document.getElementById(id), f=ms=>{const s=Math.max(0,Math.floor(ms/1000));return Math.floor(s/60)+':'+String(s%60).padStart(2,'0')};
              let dragging=false; $('vol').onpointerdown=()=>dragging=true; $('vol').onpointerup=()=>dragging=false;
              function show(s){ $('title').textContent=s.title||'Pylon'; $('artist').textContent=s.artist||'';
                $('toggle').textContent=s.playing?'⏸':'▶'; $('time').textContent=f(s.time); $('len').textContent=f(s.length);
                $('fill').style.width=(s.length?100*s.time/s.length:0)+'%'; if(!dragging) $('vol').value=s.volume; }
              async function go(c,v){ try{ show(await (await fetch('api/'+c+(v!==undefined?'?v='+v:''))).json()); }catch(e){} }
              go('state'); setInterval(()=>go('state'),1000);
            </script></body></html>
            """;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
