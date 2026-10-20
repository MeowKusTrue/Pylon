#!/usr/bin/env python3
"""Pylon Watch Together relay: lets friends anywhere watch what a host's Pylon plays, without the file.

The host's Pylon sends, per room:
  POST /r/<room>/host/state?key=K   JSON state every second; the reply carries guests' commands {"cmds": ["toggle"]}
  POST /r/<room>/host/stream?key=K  one endless chunked upload of MPEG-TS (a copy of playback, nothing re-encoded)
Guests (Pylon, or VLC with the stream address) use:
  GET  /r/<room>/                   the invite page: "Open in Pylon" (pylon://join), "Download Pylon", watch in the browser (mpegts.js), the VLC address
  GET  /r/<room>/api/state          the host's state, with "stream": "stream.ts"
  GET  /r/<room>/api/toggle?n=Ann   pause / play for everyone (the host's Pylon carries it out and says who did it)
  GET  /r/<room>/api/seek?v=MS&n=Ann  move everyone to MS milliseconds
  GET  /r/<room>/stream.ts          the live stream
The first key a room is created with owns it; a room closes 2 minutes after its host stops sending.

Python 3.8+, standard library only:  python3 party-relay.py   (port: --port, or $PORT as hosts like Render / Fly set it;
without either it takes 8787, or the next free one if that's busy, and prints which)
Put it behind nginx / Caddy for HTTPS (proxy_buffering off; proxy_request_buffering off for /host/stream).
"""
import argparse
import asyncio
import html
import json
import os
import time
import urllib.parse

ROOM_IDLE = 120          # seconds without host state before a room is dropped
GUEST_BACKLOG = 512      # chunks a slow guest may fall behind before it is cut off
MAX_ROOMS = 200


class Room:
    def __init__(self, key):
        self.key = key
        self.state = {}
        self.seen = time.monotonic()
        self.cmds = []
        self.guests = set()   # asyncio.Queue per stream listener


rooms = {}


async def read_request(reader):
    line = await reader.readline()
    if not line:
        return None
    parts = line.decode("latin-1").split()
    if len(parts) < 2:
        return None
    method, target = parts[0], parts[1]
    headers = {}
    while True:
        h = await reader.readline()
        if h in (b"\r\n", b"\n", b""):
            break
        k, _, v = h.decode("latin-1").partition(":")
        headers[k.strip().lower()] = v.strip()
    return method, target, headers


async def body_chunks(reader, headers):
    """The request body, chunk by chunk (chunked or Content-Length)."""
    if headers.get("transfer-encoding", "").lower() == "chunked":
        while True:
            size = int((await reader.readline()).split(b";")[0].strip() or b"0", 16)
            if size == 0:
                await reader.readline()
                return
            data = await reader.readexactly(size)
            await reader.readline()
            yield data
    else:
        left = int(headers.get("content-length", "0"))
        while left > 0:
            data = await reader.read(min(65536, left))
            if not data:
                return
            left -= len(data)
            yield data


def reply(writer, status, body, ctype="application/json; charset=utf-8"):
    if isinstance(body, str):
        body = body.encode()
    writer.write(f"HTTP/1.1 {status}\r\nContent-Type: {ctype}\r\nContent-Length: {len(body)}\r\nCache-Control: no-store\r\n"
                 f"Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n".encode() + body)


def page(room_url, title):
    """The invite page: open in Pylon, download Pylon, or watch right here (mpegts.js turns the MPEG-TS copy into
    something a <video> plays; H.264 everywhere, HEVC where the browser and GPU can). English or Russian by the browser."""
    t = html.escape(title or "Pylon")
    join = html.escape("pylon://join/?url=" + urllib.parse.quote(room_url, safe=""))
    stream = html.escape(room_url + "stream.ts")
    return f"""<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{t} · Pylon</title><style>
body{{margin:0;min-height:100vh;display:grid;place-items:center;background:#111;color:#eee;font:16px/1.5 system-ui,sans-serif;padding:24px;box-sizing:border-box}}
main{{width:min(960px,100%)}} h1{{font-size:24px;margin:0 0 6px}} p{{color:#aaa;margin:8px 0}}
.row{{display:flex;flex-wrap:wrap;gap:10px;margin:14px 0}}
a.b,button.b{{display:inline-block;border:0;cursor:pointer;background:#2a2a2a;color:#eee;text-decoration:none;font:600 15px system-ui,sans-serif;padding:12px 18px;border-radius:10px}}
.b.main{{background:#d37918;color:#111}}
video{{display:none;width:100%;max-height:70vh;background:#000;border-radius:12px;margin-top:8px}}
details{{color:#888;margin-top:14px}} code{{display:block;background:#1e1e1e;padding:10px 12px;border-radius:8px;word-break:break-all;color:#ddd;margin-top:6px}}
#msg{{color:#e8a35c}}
</style></head><body><main><h1 id="t">{t}</h1><p data-i="lead">Watch together: the host's picture and sound, in step.</p>
<div class="row"><a class="b main" href="{join}" data-i="open">Open in Pylon</a>
<button class="b" id="here" data-i="here">Watch in the browser</button>
<a class="b" href="https://github.com/MeowKusTrue/Pylon/releases/latest/download/Pylon-win-Setup.exe" data-i="get">Download Pylon (Windows)</a></div>
<p id="msg"></p><video id="v" controls playsinline></video>
<div class="row" id="ctl" style="display:none"><button class="b" id="pp" data-i="pp">Pause / play for everyone</button></div>
<details><summary data-i="vlc">Watch in VLC instead</summary><p data-i="vlc2">Media → Open Network Stream, this address:</p><code>{stream}</code></details>
</main>
<script src="https://cdn.jsdelivr.net/npm/mpegts.js@1.7.3/dist/mpegts.js"></script>
<script>
const ru = (navigator.language||'').toLowerCase().startsWith('ru');
const T = {{lead:'Смотреть вместе: картинка и звук хоста, синхронно.', open:'Открыть в Pylon', here:'Смотреть в браузере',
  get:'Скачать Pylon (Windows)', pp:'Пауза / продолжить у всех', vlc:'Или смотреть в VLC', vlc2:'Медиа → Открыть URL, этот адрес:',
  codec:'Этот формат видео браузер не показывает — откройте в Pylon или VLC.', wait:'Подключаюсь к потоку…', ended:'Хост остановил показ.'}};
const E = {{codec:"This video format doesn't play in the browser: open it in Pylon or VLC.", wait:'Connecting to the stream…', ended:'The host stopped sharing.'}};
if (ru) document.querySelectorAll('[data-i]').forEach(e => {{ const k = e.dataset.i; if (T[k]) e.textContent = T[k]; }});
const say = k => document.getElementById('msg').textContent = (ru ? T : E)[k] || '';
setInterval(async()=>{{try{{const s=await (await fetch('api/state')).json();document.getElementById('t').textContent=s.title||'Pylon'}}catch(e){{}}}},3000);
document.getElementById('here').onclick = () => {{
  const v = document.getElementById('v');
  if (!window.mpegts || !mpegts.getFeatureList().mseLivePlayback) {{ say('codec'); return; }}
  say('wait'); v.style.display = 'block'; document.getElementById('ctl').style.display = 'flex';
  const p = mpegts.createPlayer({{ type: 'mpegts', isLive: true, url: 'stream.ts' }}, {{ liveBufferLatencyChasing: true, enableStashBuffer: false }});
  p.attachMediaElement(v); p.load();
  p.on(mpegts.Events.ERROR, (type, detail) => say(detail === 'CodecUnsupported' || type === 'MediaError' ? 'codec' : 'ended'));
  v.addEventListener('playing', () => say(''));
  p.play().catch(() => {{}});
}};
document.getElementById('pp').onclick = () => fetch('api/toggle?n=' + encodeURIComponent(ru ? 'Гость в браузере' : 'Browser guest'));
</script></body></html>"""


async def handle(reader, writer):
    try:
        req = await asyncio.wait_for(read_request(reader), 15)
        if not req:
            return
        method, target, headers = req
        url = urllib.parse.urlsplit(target)
        query = dict(urllib.parse.parse_qsl(url.query))
        parts = [p for p in url.path.split("/") if p]
        if len(parts) < 2 or parts[0] != "r" or not parts[1].isalnum() or len(parts[1]) > 32:
            reply(writer, "404 Not Found", '{"error":"no such room"}')
            return
        name, rest = parts[1], "/".join(parts[2:])
        room = rooms.get(name)

        if rest.startswith("host/"):
            key = query.get("key", "")
            if room is None:
                if len(rooms) >= MAX_ROOMS or len(key) < 16:
                    reply(writer, "403 Forbidden", '{"error":"full"}')
                    return
                room = rooms[name] = Room(key)
            if key != room.key:
                reply(writer, "403 Forbidden", '{"error":"not your room"}')
                return
            room.seen = time.monotonic()
            if rest == "host/state" and method == "POST":
                data = b"".join([c async for c in body_chunks(reader, headers)])
                try:
                    room.state = json.loads(data or b"{}")
                except ValueError:
                    pass
                cmds, room.cmds = room.cmds, []
                reply(writer, "200 OK", json.dumps({"cmds": cmds}))
            elif rest == "host/stream" and method == "POST":
                async for chunk in body_chunks(reader, headers):
                    room.seen = time.monotonic()
                    for q in list(room.guests):
                        if q.qsize() >= GUEST_BACKLOG:
                            room.guests.discard(q)
                            q.put_nowait(None)          # too slow: cut off
                        else:
                            q.put_nowait(chunk)
                reply(writer, "200 OK", "{}")
            else:
                reply(writer, "404 Not Found", "{}")
            return

        if room is None:
            reply(writer, "404 Not Found", '{"error":"the host left"}')
            return
        if rest == "":
            proto = headers.get("x-forwarded-proto", "http")
            host = headers.get("x-forwarded-host") or headers.get("host", "")
            reply(writer, "200 OK", page(f"{proto}://{host}/r/{name}/", room.state.get("title")), "text/html; charset=utf-8")
        elif rest in ("api/state", "api/toggle", "api/seek"):
            if rest != "api/state" and len(room.cmds) < 10:
                value = query.get("v", "0")
                who = query.get("n", "")[:40].replace(":", " ")
                # a bare "toggle" without a name: what Pylon 1.9.7 hosts understand
                room.cmds.append("toggle" if rest == "api/toggle" and not who else f"{rest[4:]}:{value if value.isdigit() else 0}:{who}")
            reply(writer, "200 OK", json.dumps({**room.state, "stream": "stream.ts"}))
        elif rest == "stream.ts":
            q = asyncio.Queue()
            room.guests.add(q)
            writer.write(b"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n")
            try:
                while True:
                    chunk = await q.get()
                    if chunk is None:
                        break
                    writer.write(chunk)
                    await writer.drain()
            finally:
                room.guests.discard(q)
        else:
            reply(writer, "404 Not Found", "{}")
    except (asyncio.TimeoutError, asyncio.IncompleteReadError, ConnectionError, ValueError):
        pass
    finally:
        try:
            await writer.drain()
            writer.close()
        except (ConnectionError, RuntimeError):
            pass


async def sweep():
    while True:
        await asyncio.sleep(30)
        now = time.monotonic()
        for name, room in list(rooms.items()):
            if now - room.seen > ROOM_IDLE:
                for q in room.guests:
                    q.put_nowait(None)
                del rooms[name]


async def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=int(os.environ["PORT"]) if os.environ.get("PORT") else None)
    a = ap.parse_args()
    ports = [a.port] if a.port else range(8787, 8807)   # asked for one: that one or nothing
    server = None
    for port in ports:
        try:
            server = await asyncio.start_server(handle, a.host, port)
            break
        except OSError as e:
            if a.port:
                raise SystemExit(f"Port {port} is busy ({e.strerror}). Pick another with --port.")
    if server is None:
        raise SystemExit("Ports 8787-8806 are all busy. Pick one with --port.")
    print(f"Pylon party relay on {a.host}:{port}", flush=True)
    asyncio.create_task(sweep())
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
