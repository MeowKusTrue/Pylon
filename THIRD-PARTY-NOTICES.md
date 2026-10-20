# Third-party notices

Pylon is licensed under the GNU GPL v3 (see `LICENSE`). It ships or uses:

| Component | License | Source |
|---|---|---|
| libVLC (VideoLAN.LibVLC.Windows) — the playback engine | LGPL-2.1-or-later (plugins: LGPL/GPL) | https://code.videolan.org/videolan/vlc |
| LibVLCSharp / LibVLCSharp.WPF | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| SharpVectors | BSD-3-Clause | https://github.com/ElinamLLC/SharpVectors |
| Phosphor Icons | MIT | https://phosphoricons.com |
| QRCoder | MIT | https://github.com/Shane32/QRCoder |
| TagLibSharp — the tag editor | LGPL-2.1 | https://github.com/mono/taglib-sharp |
| yt-dlp — not shipped; downloaded from its GitHub releases on first use, into %LocalAppData%\Pylon\tools | Unlicense | https://github.com/yt-dlp/yt-dlp |
| FFmpeg — not shipped; downloaded on consent (BtbN builds) for tools that need it, into %LocalAppData%\Pylon\tools | GPL-3.0 (these builds) | https://github.com/BtbN/FFmpeg-Builds |
| whisper.cpp and its speech models — not shipped; downloaded on consent for "Subtitles from speech", into %LocalAppData%\Pylon\tools\whisper | MIT | https://github.com/ggml-org/whisper.cpp |
| mpegts.js — loaded by the Watch Together invite page (relay) from jsDelivr | Apache-2.0 | https://github.com/xqq/mpegts.js |

libVLC is loaded as separate DLLs from the `libvlc` folder and can be replaced with another build of the same version.

The interface is based on the Figma Community file "VLC Redesign" by Juxtopposed (https://www.figma.com/community/file/1691465999134370980/vlc-redesign; video: https://youtu.be/ERFeOgqk324).
Internet radio listings come from https://www.radio-browser.info. Lyrics come from files next to the track, LRCLIB
(https://lrclib.net) and, with your own API key, Spicy Lyrics (https://spicylyrics.org), shown with the attribution
they ask for; Spotify ids for Spicy Lyrics are looked up through the Deezer and Spotify Web APIs.

VLC and the VLC cone are trademarks of VideoLAN. Pylon is an independent project and is not affiliated with or endorsed by VideoLAN.
