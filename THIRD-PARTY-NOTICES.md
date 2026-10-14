# Third-party notices

Pylon is licensed under the GNU GPL v3 (see `LICENSE`). It ships or uses:

| Component | License | Source |
|---|---|---|
| libVLC (VideoLAN.LibVLC.Windows) — the playback engine | LGPL-2.1-or-later (plugins: LGPL/GPL) | https://code.videolan.org/videolan/vlc |
| LibVLCSharp / LibVLCSharp.WPF | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| SharpVectors | BSD-3-Clause | https://github.com/ElinamLLC/SharpVectors |
| Phosphor Icons | MIT | https://phosphoricons.com |
| QRCoder | MIT | https://github.com/Shane32/QRCoder |

libVLC is loaded as separate DLLs from the `libvlc` folder and can be replaced with another build of the same version.

The interface is based on the Figma Community file "VLC Redesign" by Juxtopposed (https://www.figma.com/community/file/1691465999134370980/vlc-redesign; video: https://youtu.be/ERFeOgqk324).
Internet radio listings come from https://www.radio-browser.info. Lyrics come from files next to the track, LRCLIB
(https://lrclib.net) and, with your own API key, Spicy Lyrics (https://spicylyrics.org), shown with the attribution
they ask for; Spotify ids for Spicy Lyrics are looked up through the Deezer and Spotify Web APIs.

VLC and the VLC cone are trademarks of VideoLAN. Pylon is an independent project and is not affiliated with or endorsed by VideoLAN.
