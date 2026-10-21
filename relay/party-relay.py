#!/usr/bin/env python3
"""Pylon Watch Together relay: lets friends anywhere watch what a host's Pylon plays, without the file.

The host's Pylon sends, per room:
  POST /r/<room>/host/state?key=K   JSON state every second; the reply carries guests' commands and the viewer count {"cmds": ["toggle"], "viewers": 2}
  POST /r/<room>/host/stream?key=K  one endless chunked upload of MPEG-TS (a copy of playback, nothing re-encoded)
Guests (Pylon, or VLC with the stream address) use:
  GET  /r/<room>/                   the room's page: a browser player (mpegts.js) with shared play / pause and timeline, "Open in Pylon", "Get Pylon", the VLC address
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
        self.seen_by = {}     # viewer -> when last seen (see viewers())


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


# The room's page: a player for the browser (mpegts.js turns the MPEG-TS copy into something a <video> plays: H.264
# everywhere, HEVC where the browser and GPU can), with play / pause and the timeline shared with everyone, plus "Open in
# Pylon" and "Get Pylon". English or Russian by the browser. __TITLE__ / __JOIN__ / __STREAM__ are filled in per room.
ROOM_PAGE = r'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#0e0e10"><title>__TITLE__ · Pylon</title>
<style>
:root{--bg:#0e0e10;--panel:#17171a;--raise:#1f1f23;--line:#ffffff14;--text:#f2f2f3;--muted:#a3a3ab;--dim:#6e6e78;--accent:#e2862a;--accent2:#f3a552;--live:#ef4444;--r:16px}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;-webkit-font-smoothing:antialiased}
button{font:inherit;color:inherit;background:none;border:0;cursor:pointer;padding:0}
a{color:inherit;text-decoration:none}
.wrap{max-width:1120px;margin:0 auto;padding:16px 16px 40px}
header{display:flex;align-items:center;gap:12px;margin-bottom:14px}
.brand{display:flex;align-items:center;gap:9px;font-weight:700;letter-spacing:.2px}
.brand svg{width:26px;height:26px}
.live{margin-left:auto;display:flex;align-items:center;gap:8px;background:var(--panel);border:1px solid var(--line);padding:6px 12px;border-radius:999px;font-size:13px;color:var(--muted);white-space:nowrap}
.dot{width:8px;height:8px;border-radius:50%;background:var(--dim)}.live.on .dot{background:var(--live);box-shadow:0 0 0 0 #ef444488;animation:pulse 1.8s infinite}
@keyframes pulse{70%{box-shadow:0 0 0 7px #ef444400}100%{box-shadow:0 0 0 0 #ef444400}}
.stage{position:relative;aspect-ratio:16/9;background:#000;border-radius:var(--r);overflow:hidden;border:1px solid var(--line);user-select:none}
.stage video{position:absolute;inset:0;width:100%;height:100%;object-fit:contain;background:#000}
.art{position:absolute;inset:0;display:none;flex-direction:column;align-items:center;justify-content:center;gap:6px;text-align:center;padding:24px;
  background:radial-gradient(120% 90% at 50% 0%,#e2862a2e,transparent 60%),radial-gradient(90% 80% at 50% 100%,#6b3bd024,transparent 60%),#0b0b0d}
.art.on{display:flex}.art canvas{width:min(560px,90%);height:34%;margin-bottom:10px}
.art .t{font-size:clamp(20px,3.4vw,34px);font-weight:700;line-height:1.15;max-width:90%}.art .s{color:var(--muted);font-size:clamp(14px,2vw,18px)}
.start{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;background:#000a;backdrop-filter:blur(6px);transition:opacity .25s}
.start.gone{opacity:0;pointer-events:none}
.big{display:flex;align-items:center;gap:10px;background:var(--accent);color:#14100a;font-weight:700;font-size:17px;padding:14px 26px;border-radius:999px;box-shadow:0 10px 30px #e2862a40;transition:transform .15s,background .15s}
.big:hover{background:var(--accent2);transform:translateY(-1px)}.big:active{transform:scale(.98)}
.big svg{width:20px;height:20px}.hint{color:var(--muted);font-size:13px}
.note{position:absolute;left:50%;top:16px;transform:translate(-50%,-8px);opacity:0;background:#000c;border:1px solid var(--line);padding:8px 14px;border-radius:999px;font-size:14px;transition:.25s;pointer-events:none;white-space:nowrap;max-width:90%;overflow:hidden;text-overflow:ellipsis}
.note.on{opacity:1;transform:translate(-50%,0)}
.status{position:absolute;inset:auto 0 50% 0;text-align:center;color:var(--muted);font-size:14px;pointer-events:none}
.bar{position:absolute;left:0;right:0;bottom:0;padding:28px 14px 12px;background:linear-gradient(transparent,#000d);display:flex;flex-direction:column;gap:8px;transition:opacity .25s}
.bar[hidden]{display:none}.stage.idle .bar{opacity:0}.stage.idle{cursor:none}
.seek{position:relative;height:16px;display:flex;align-items:center;cursor:pointer}
.seek .rail{position:absolute;left:0;right:0;height:4px;border-radius:4px;background:#ffffff2e}
.seek .fill{position:absolute;left:0;height:4px;border-radius:4px;background:var(--accent);width:0}
.seek .knob{position:absolute;width:12px;height:12px;border-radius:50%;background:#fff;transform:translateX(-50%) scale(0);transition:transform .15s;left:0}
.seek:hover .knob{transform:translateX(-50%) scale(1)}.seek:hover .rail,.seek:hover .fill{height:6px}
.seek .tip{position:absolute;bottom:18px;transform:translateX(-50%);background:#000d;padding:2px 7px;border-radius:6px;font-size:12px;display:none}.seek:hover .tip{display:block}
.row{display:flex;align-items:center;gap:6px}
.ic{width:38px;height:38px;border-radius:10px;display:grid;place-items:center;transition:background .15s}.ic:hover{background:#ffffff1a}.ic svg{width:22px;height:22px}
.time{font-variant-numeric:tabular-nums;font-size:13px;color:#e6e6e8;margin:0 6px;white-space:nowrap}
.vol{width:84px;accent-color:var(--accent)}.grow{flex:1}
.who{font-size:12px;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:40%}
.below{display:grid;grid-template-columns:1fr auto;gap:16px;align-items:start;margin-top:16px}
.now .t{font-size:20px;font-weight:700;line-height:1.2}.now .s{color:var(--muted);margin-top:4px}
.acts{display:flex;flex-wrap:wrap;gap:8px;justify-content:flex-end}
.btn{display:inline-flex;align-items:center;gap:8px;background:var(--raise);border:1px solid var(--line);padding:10px 14px;border-radius:12px;font-weight:600;font-size:14px;transition:background .15s}
.btn:hover{background:#2a2a30}.btn.acc{background:var(--accent);color:#14100a;border-color:transparent}.btn.acc:hover{background:var(--accent2)}
.btn svg{width:17px;height:17px}
.more{margin-top:18px;color:var(--muted);font-size:14px}.more summary{cursor:pointer}.more code{display:block;margin-top:8px;background:var(--panel);border:1px solid var(--line);padding:10px 12px;border-radius:10px;word-break:break-all;color:var(--text)}
dialog{background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:16px;padding:20px;width:min(360px,92vw)}
dialog::backdrop{background:#000a}dialog input{width:100%;margin:12px 0 16px;padding:11px 12px;border-radius:10px;border:1px solid var(--line);background:var(--bg);color:var(--text);font:inherit}
dialog .r{display:flex;gap:8px;justify-content:flex-end}
@media (max-width:640px){.below{grid-template-columns:1fr}.acts{justify-content:flex-start}.vol,.who{display:none}.wrap{padding:10px 10px 30px}.stage{border-radius:12px}}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
</style></head><body><div class="wrap">
<header><div class="brand"><svg viewBox="0 0 32 32"><rect width="32" height="32" rx="9" fill="#e2862a"/><path d="M12 9v14l11-7z" fill="#14100a"/></svg>Pylon</div>
<div class="live" id="live"><span class="dot"></span><span id="liveTxt">…</span></div></header>

<div class="stage" id="stage">
  <video id="v" playsinline></video>
  <div class="art" id="art"><canvas id="viz" width="1120" height="260"></canvas><div class="t" id="artT"></div><div class="s" id="artS"></div></div>
  <div class="status" id="status"></div>
  <div class="note" id="note"></div>
  <div class="start" id="start">
    <button class="big" id="go"><svg viewBox="0 0 24 24"><path d="M7 4.5v15l13-7.5z" fill="currentColor"/></svg><span data-i="watch">Watch together</span></button>
    <div class="hint" data-i="hint">The host's picture and sound, in step with everyone</div>
  </div>
  <div class="bar" id="bar" hidden>
    <div class="seek" id="seek"><div class="rail"></div><div class="fill" id="fill"></div><div class="knob" id="knob"></div><div class="tip" id="tip"></div></div>
    <div class="row">
      <button class="ic" id="pp" title="Play / pause for everyone (Space)"><svg viewBox="0 0 24 24" id="ppIc"></svg></button>
      <span class="time"><span id="cur">0:00</span> / <span id="dur">0:00</span></span>
      <span class="who" id="who"></span><span class="grow"></span>
      <button class="ic" id="mute" title="Mute (M)"><svg viewBox="0 0 24 24" id="muteIc"></svg></button>
      <input class="vol" id="vol" type="range" min="0" max="1" step="0.01" value="1">
      <button class="ic" id="pip" title="Picture in picture"><svg viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="14" rx="2" fill="none" stroke="currentColor" stroke-width="1.8"/><rect x="12" y="11" width="7" height="6" rx="1" fill="currentColor"/></svg></button>
      <button class="ic" id="fs" title="Fullscreen (F)"><svg viewBox="0 0 24 24"><path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg></button>
    </div>
  </div>
</div>

<div class="below">
  <div class="now"><div class="t" id="title">__TITLE__</div><div class="s" id="sub"></div></div>
  <div class="acts">
    <a class="btn acc" href="__JOIN__"><svg viewBox="0 0 24 24"><path d="M7 4.5v15l13-7.5z" fill="currentColor"/></svg><span data-i="open">Open in Pylon</span></a>
    <a class="btn" href="https://github.com/MeowKusTrue/Pylon/releases/latest/download/Pylon-win-Setup.exe"><svg viewBox="0 0 24 24"><path d="M12 4v11m0 0-4-4m4 4 4-4M5 19h14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg><span data-i="get">Get Pylon</span></a>
    <button class="btn" id="nameBtn"><svg viewBox="0 0 24 24"><circle cx="12" cy="8" r="4" fill="none" stroke="currentColor" stroke-width="1.8"/><path d="M4 20c1.5-4 4.5-6 8-6s6.5 2 8 6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg><span id="nameTxt"></span></button>
  </div>
</div>
<details class="more"><summary data-i="vlc">Watch in VLC instead</summary><div data-i="vlc2">Media → Open Network Stream, this address:</div><code>__STREAM__</code></details>
</div>

<dialog id="nameDlg"><b data-i="nameQ">Your name</b><div class="hint" data-i="nameWhy">The others see it when you pause or jump.</div>
<input id="nameIn" maxlength="30" autocomplete="nickname"><div class="r"><button class="btn" id="nameNo" data-i="cancel">Cancel</button><button class="btn acc" id="nameOk" data-i="save">Save</button></div></dialog>

<script src="https://cdn.jsdelivr.net/npm/mpegts.js@1.7.3/dist/mpegts.js"></script>
<script>
(() => {
const ru = (navigator.language || '').toLowerCase().startsWith('ru');
const RU = {watch:'Смотреть вместе', hint:'Картинка и звук хоста — у всех одновременно', open:'Открыть в Pylon', get:'Скачать Pylon',
  vlc:'Или смотреть в VLC', vlc2:'Медиа → Открыть URL, этот адрес:', nameQ:'Как вас зовут', nameWhy:'Остальные увидят имя, когда вы поставите паузу или перемотаете.',
  cancel:'Отмена', save:'Сохранить'};
const S = ru ? {live:n => n > 1 ? `В эфире · ${n} смотрят` : 'В эфире', off:'Хост не в эфире', wait:'Подключаюсь…', again:'Переподключаюсь…',
  codec:'Браузер не показывает этот формат — откройте в Pylon или VLC.', paused:w => w ? `${w} поставил(а) на паузу` : 'На паузе', host:h => `Ведёт ${h}`,
  guest:'Гость', name:n => n, music:'Музыка'} :
  {live:n => n > 1 ? `Live · ${n} watching` : 'Live', off:'The host is offline', wait:'Connecting…', again:'Reconnecting…',
  codec:"This format doesn't play in the browser: open it in Pylon or VLC.", paused:w => w ? `${w} paused` : 'Paused', host:h => `Hosted by ${h}`,
  guest:'Guest', name:n => n, music:'Music'};
if (ru) { document.documentElement.lang = 'ru'; document.querySelectorAll('[data-i]').forEach(e => { if (RU[e.dataset.i]) e.textContent = RU[e.dataset.i]; }); }
const $ = id => document.getElementById(id);
const v = $('v'), stage = $('stage');
const store = { get(k) { try { return localStorage.getItem(k); } catch { return null; } }, set(k, x) { try { localStorage.setItem(k, x); } catch {} } };
let name = store.get('pylon-name') || '';
const myName = () => name || S.guest;
const showName = () => $('nameTxt').textContent = myName();
showName();

const ICON = {play:'<path d="M7 4.5v15l13-7.5z" fill="currentColor"/>', pause:'<rect x="6" y="4.5" width="4.2" height="15" rx="1.2" fill="currentColor"/><rect x="13.8" y="4.5" width="4.2" height="15" rx="1.2" fill="currentColor"/>',
  vol:'<path d="M4 9h4l5-4v14l-5-4H4z" fill="currentColor"/><path d="M16 8.5a5 5 0 0 1 0 7M18.5 6a8.5 8.5 0 0 1 0 12" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>',
  mute:'<path d="M4 9h4l5-4v14l-5-4H4z" fill="currentColor"/><path d="m16 9 5 6m0-6-5 6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>'};
const fmt = ms => { const s = Math.max(0, Math.floor(ms / 1000)), h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = String(s % 60).padStart(2, '0'); return h ? `${h}:${String(m).padStart(2, '0')}:${x}` : `${m}:${x}`; };

// ── the host's state: title, play / pause, position (interpolated between polls), notes, viewers
let st = {}, stAt = 0, lastNote = -1, file = null, started = false;
const hostTime = () => Math.min(st.length || Infinity, (st.time || 0) + (st.playing ? performance.now() - stAt : 0));
async function poll() {
  try {
    const r = await fetch('api/state?n=' + encodeURIComponent(myName()), { cache: 'no-store' });
    if (!r.ok) throw 0;
    const s = await r.json(); st = s; stAt = performance.now();
    $('live').classList.add('on'); $('liveTxt').textContent = S.live(s.viewers || 1);
    const title = s.title || 'Pylon';
    if ($('title').textContent !== title) { $('title').textContent = title; document.title = title + ' · Pylon'; }
    $('sub').textContent = [s.artist, s.host ? S.host(s.host) : ''].filter(Boolean).join(' · ');
    $('artT').textContent = title; $('artS').textContent = s.artist || '';
    $('ppIc').innerHTML = s.playing ? ICON.pause : ICON.play;
    $('who').textContent = !s.playing && s.pausedBy ? S.paused(s.pausedBy) : '';
    if (s.noteId !== undefined && s.noteId !== lastNote) { if (lastNote >= 0 && s.note && s.noteBy !== myName()) toast(s.note); lastNote = s.noteId; }
    if (started && s.file && file && s.file !== file) restart(800); // the host's next track: its timestamps start over
    file = s.file || file;
  } catch { $('live').classList.remove('on'); $('liveTxt').textContent = S.off; }
}
setInterval(poll, 1000); poll();
let toastT; function toast(t) { const n = $('note'); n.textContent = t; n.classList.add('on'); clearTimeout(toastT); toastT = setTimeout(() => n.classList.remove('on'), 3500); }

// ── the stream: mpegts.js plays the MPEG-TS copy; it is opened again on a new track, an error or a picture that stands still
let player = null, retry = 0, restartT = 0, lastT = 0, movedAt = 0;
const status = t => $('status').textContent = t || '';
function open() {
  if (player) { try { player.destroy(); } catch {} player = null; }
  if (!window.mpegts || !mpegts.getFeatureList().mseLivePlayback) { status(S.codec); return; }
  player = mpegts.createPlayer({ type: 'mpegts', isLive: true, url: 'stream.ts' },
    { liveBufferLatencyChasing: true, liveBufferLatencyMaxLatency: 3, liveBufferLatencyMinRemain: 0.6, enableStashBuffer: false, lazyLoad: false });
  player.attachMediaElement(v); player.load();
  player.on(mpegts.Events.ERROR, (type, detail) => { if (detail === 'CodecUnsupported') { status(S.codec); return; } restart(Math.min(8000, 1000 * ++retry)); });
  movedAt = performance.now();
  v.play().catch(() => {});
}
function restart(ms) { clearTimeout(restartT); status(S.again); restartT = setTimeout(open, ms); }
v.addEventListener('playing', () => { status(''); retry = 0; });
setInterval(() => { // a stand-still while the host plays: open it again
  if (!started || !player) return;
  $('art').classList.toggle('on', v.readyState >= 1 && v.videoWidth === 0); // music: no picture, the visualizer instead
  if (v.currentTime !== lastT) { lastT = v.currentTime; movedAt = performance.now(); }
  else if (st.playing && performance.now() - movedAt > 5000) { movedAt = performance.now(); restart(0); }
  // stay near the live edge: a browser that fell behind (a busy tab) jumps forward
  if (v.buffered.length && v.buffered.end(v.buffered.length - 1) - v.currentTime > 4) v.currentTime = v.buffered.end(v.buffered.length - 1) - 0.8;
}, 1000);

// ── the visualizer for music (no picture): the sound's spectrum, in the accent colour
let actx, analyser, bins;
function visualize() {
  try { actx = new (window.AudioContext || window.webkitAudioContext)(); const src = actx.createMediaElementSource(v); analyser = actx.createAnalyser(); analyser.fftSize = 256; src.connect(analyser); analyser.connect(actx.destination); bins = new Uint8Array(analyser.frequencyBinCount); } catch { return; }
  const c = $('viz'), g = c.getContext('2d');
  (function draw() {
    requestAnimationFrame(draw);
    if (!$('art').classList.contains('on')) return;
    analyser.getByteFrequencyData(bins);
    g.clearRect(0, 0, c.width, c.height);
    const n = 48, w = c.width / n;
    for (let i = 0; i < n; i++) {
      const x = bins[Math.min(bins.length - 1, Math.floor(Math.pow(bins.length * 0.75, i / (n - 1))))] / 255 // log scale: the lows don't take every bar, h = Math.max(4, x * c.height);
      g.fillStyle = `hsl(${28 + i * 0.6} 80% ${48 + x * 18}%)`;
      g.beginPath(); g.roundRect ? g.roundRect(i * w + 3, c.height - h, w - 6, h, 4) : g.rect(i * w + 3, c.height - h, w - 6, h); g.fill();
    }
  })();
}

// ── controls
$('go').onclick = () => { started = true; $('start').classList.add('gone'); $('bar').hidden = false; status(S.wait); visualize(); open(); };
const toggle = () => fetch('api/toggle?n=' + encodeURIComponent(myName()));
$('pp').onclick = toggle;
v.addEventListener('click', toggle); $('art').addEventListener('click', toggle);
const setVol = x => { v.volume = x; v.muted = x === 0; $('vol').value = x; $('muteIc').innerHTML = v.muted ? ICON.mute : ICON.vol; store.set('pylon-vol', x); };
setVol(+(store.get('pylon-vol') ?? 1));
$('vol').oninput = e => setVol(+e.target.value);
$('mute').onclick = () => { v.muted = !v.muted; $('muteIc').innerHTML = v.muted ? ICON.mute : ICON.vol; };
$('fs').onclick = () => document.fullscreenElement ? document.exitFullscreen() : stage.requestFullscreen?.();
$('pip').onclick = () => document.pictureInPictureElement ? document.exitPictureInPicture() : v.requestPictureInPicture?.().catch(() => {});
if (!document.pictureInPictureEnabled) $('pip').style.display = 'none';
// the timeline is the host's: a click moves everyone there
const seek = $('seek');
const at = e => { const r = seek.getBoundingClientRect(); return Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)); };
seek.addEventListener('mousemove', e => { const p = at(e); $('tip').style.left = p * 100 + '%'; $('tip').textContent = fmt(p * (st.length || 0)); });
seek.addEventListener('click', e => { if (st.length) { fetch(`api/seek?v=${Math.round(at(e) * st.length)}&n=${encodeURIComponent(myName())}`); st.time = at(e) * st.length; stAt = performance.now(); } });
(function tick() { requestAnimationFrame(tick); const t = hostTime(), p = st.length ? t / st.length * 100 : 0;
  $('fill').style.width = p + '%'; $('knob').style.left = p + '%'; $('cur').textContent = fmt(t); $('dur').textContent = fmt(st.length || 0); })();
// the bar hides while watching
let idleT; const wake = () => { stage.classList.remove('idle'); clearTimeout(idleT); idleT = setTimeout(() => { if (started) stage.classList.add('idle'); }, 2600); };
stage.addEventListener('mousemove', wake); stage.addEventListener('touchstart', wake, { passive: true }); wake();
document.addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || $('nameDlg').open) return;
  if (e.code === 'Space' && started) { e.preventDefault(); toggle(); }
  else if (e.key === 'f' || e.key === 'а') $('fs').click();
  else if (e.key === 'm' || e.key === 'ь') $('mute').click();
});
// name
$('nameBtn').onclick = () => { $('nameIn').value = name; $('nameDlg').showModal(); $('nameIn').focus(); };
$('nameNo').onclick = () => $('nameDlg').close();
$('nameOk').onclick = () => { name = $('nameIn').value.trim().slice(0, 30); store.set('pylon-name', name); showName(); $('nameDlg').close(); };
$('nameIn').addEventListener('keydown', e => { if (e.key === 'Enter') $('nameOk').click(); });
})();
</script></body></html>
'''


def page(room_url, title):
    t = html.escape(title or "Pylon")
    join = html.escape("pylon://join/?url=" + urllib.parse.quote(room_url, safe=""))
    stream = html.escape(room_url + "stream.ts")
    return ROOM_PAGE.replace("__TITLE__", t).replace("__JOIN__", join).replace("__STREAM__", stream)


VIEWER_TIMEOUT = 6       # seconds: a viewer counts while its page / Pylon asks for the state or reads the stream


def viewer(writer, headers):
    """Who is asking, for counting viewers: their address (behind nginx, the forwarded one) and their browser."""
    peer = writer.get_extra_info("peername")
    ip = headers.get("x-real-ip") or headers.get("x-forwarded-for", "").split(",")[0].strip() or (peer[0] if peer else "")
    return ip + "|" + headers.get("user-agent", "")[:60]


def viewers(room):
    now = time.monotonic()
    for k in [k for k, t in room.seen_by.items() if now - t > VIEWER_TIMEOUT]:
        del room.seen_by[k]
    return len(room.seen_by)


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
                reply(writer, "200 OK", json.dumps({"cmds": cmds, "viewers": viewers(room)}))
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
            room.seen_by[viewer(writer, headers)] = time.monotonic()
            if rest != "api/state" and len(room.cmds) < 10:
                value = query.get("v", "0")
                who = query.get("n", "")[:40].replace(":", " ")
                # a bare "toggle" without a name: what Pylon 1.9.7 hosts understand
                room.cmds.append("toggle" if rest == "api/toggle" and not who else f"{rest[4:]}:{value if value.isdigit() else 0}:{who}")
            reply(writer, "200 OK", json.dumps({**room.state, "stream": "stream.ts", "viewers": viewers(room)}))
        elif rest == "stream.ts":
            q = asyncio.Queue()
            room.guests.add(q)
            who = viewer(writer, headers)
            writer.write(b"HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n")
            try:
                while True:
                    chunk = await q.get()
                    if chunk is None:
                        break
                    room.seen_by[who] = time.monotonic()   # VLC and other players that only read the stream count too
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
