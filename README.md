<p align="center">
  <img src="Pylon/Assets/Icons/logo.svg" width="96" alt="Pylon logo">
</p>

<h1 align="center">Pylon</h1>

<p align="center">A Windows media player built on the VLC engine (libVLC), with a redesigned VLC interface.</p>

![Music playback with the library sidebar](docs/screenshots/music.png)

Pylon brings the community **"VLC Redesign"** Figma concept to life as a real player. Under the hood it's libVLC 3, so it plays what VLC plays: almost every audio and video format, discs, network streams.

## Features

- **Player:** rounded video frame, music mode with blurred cover art, waveform seek bar with frame previews, fullscreen with floating controls, a "hide UI" mode, and a miniplayer.
- **Library:** Home (Continue Watching, Listen Again), video and music folders, albums, artists and genres from tags, playlists with covers, history, and the play queue.
- **Network:** network streams, devices on your local network (UPnP / mDNS), podcasts (RSS), and internet radio from radio-browser.info.
- **Settings:** 13 categories and about 150 options. They apply instantly; when libVLC must restart, playback resumes where it was.
- **Effects:** a 10-band equalizer with presets, compressor, spatializer, stereo widener, pitch shift, and karaoke. For video: color, crop and rotate.
- **Themes:** dark, light, or follow Windows, plus 9 accent colors.
- **Controls:** keyboard shortcuts you can rebind (also system-wide), mouse-wheel and click actions, and a control bar you choose the buttons for.
- **Also:** record (`Shift+R`), snapshots (`Shift+S`), Chromecast, subtitle matching across a whole season, profiles, and a single instance.
- **Windows integration:** the installer registers audio and video file types and adds "Play with Pylon" to folders.

| | |
|---|---|
| ![Settings](docs/screenshots/settings.png) | ![Effects](docs/screenshots/fx.png) |
| ![Light theme](docs/screenshots/light.png) | ![Onboarding](docs/screenshots/onb.png) |

## Install

Download from [Releases](../../releases):

- `Pylon-x.y.z-setup-win-x64.exe` is the installer. It installs per user, without admin rights, and sets up file associations.
- `Pylon-x.y.z-win-x64-portable.zip` runs from any folder, but doesn't register file types.

Windows doesn't let apps set themselves as the default player. To choose Pylon, tick the last box in the installer, or go to **Settings → Library → Default player**.

Requires Windows 10 or 11, x64. The .NET runtime is bundled.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The installer also needs [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
dotnet run --project Pylon          # run from source
.\build.ps1                         # dist\: portable zip + installer
.\build.ps1 -Version 1.1.0
```

Keep the checkout in a short path, such as `D:\Projects\Pylon`. libVLC 3 can't load plugins from paths longer than 260 characters, and you would lose sound.

Icons come from [Phosphor](https://phosphoricons.com). To add one, use its name in code and run `python Pylon/tools/sync-icons.py <path-to-@phosphor-icons/core/assets>`.

## Credits

- [VLC / libVLC](https://www.videolan.org) and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) by VideoLAN and contributors.
- The interface follows the community **VLC Redesign** Figma file.
- [Phosphor Icons](https://phosphoricons.com), [SharpVectors](https://github.com/ElinamLLC/SharpVectors), and [radio-browser.info](https://www.radio-browser.info).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[GPL-3.0](LICENSE). libVLC and LibVLCSharp are LGPL-2.1.

VLC and the VLC cone are trademarks of VideoLAN. Pylon is an independent project and is not affiliated with or endorsed by VideoLAN.
