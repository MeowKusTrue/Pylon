<p align="center">
  <img src="docs/brand/pylon-tile.svg" width="96" alt="Pylon logo">
</p>

<h1 align="center">Pylon</h1>

<p align="center">A Windows media player built on the VLC engine (libVLC), with a redesigned VLC interface.</p>

<p align="center"><a href="README.ru.md">Читать на русском</a></p>

![Music playback with the library sidebar](docs/screenshots/music.png)

Pylon brings the community **"VLC Redesign"** Figma concept to life as a real player. Under the hood it's libVLC 3, so it plays what VLC plays: almost every audio and video format, discs, network streams.

## Features

- **Player:** rounded video frame, music mode with blurred cover art, waveform seek bar with frame previews, fullscreen with floating controls, a "hide UI" mode, and a miniplayer.
- **Library:** Home (Continue Watching, Listen Again), video and music folders, albums, artists and genres from tags, playlists with covers, history, and the play queue.
- **Network:** network streams, devices on your local network (UPnP / mDNS), podcasts (RSS), and internet radio from radio-browser.info.
- **Settings:** 13 categories and about 150 options. They apply instantly; when libVLC must restart, playback resumes where it was.
- **Effects:** a 10-band equalizer with presets, compressor, spatializer, stereo widener, pitch shift, and karaoke. For video: color, crop and rotate.
- **Themes:** dark, light, follow Windows, OLED black, Midnight, Nord and Sepia, plus 9 accent colors and adjustable corner rounding (including the window corners on Windows 11).
- **Playback tools:** A-B loop (`Shift+L`, range shown on the seek bar), sleep timer with a gentle fade-out, a real waveform on the music seek bar (decoded once in the background, cached).
- **Motion:** quick ease-out transitions for panels, menus, dialogs and library pages; hotkey actions stay instant, and Windows' "Animation effects" switch turns them off.
- **Audio spectrum:** live bars under the cover art, optionally over video too. Only Pylon's own sound is captured (WASAPI process loopback, Windows 10 2004+).
- **Languages:** English and Russian (Settings → General → Interface language).
- **Updates:** checks GitHub Releases once a day and updates itself in place.
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

## Updates

Pylon checks [the latest release](../../releases/latest) once a day. You can turn this off in **Settings → General → Check for updates automatically**, or check yourself with **… → Check for Updates…**.

- **Installed copy:** click **Update now**. Pylon downloads the new setup, installs it silently, and restarts.
- **Portable copy:** the update opens the release page so you can download the new zip.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The installer also needs [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
dotnet run --project Pylon          # run from source
.\build.ps1                         # dist\: portable zip + installer
.\build.ps1 -Version 1.1.0
```

### Releasing a new version

Releases are built by GitHub Actions (`.github/workflows/release.yml`). Every push to `main` is also build-checked by `ci.yml`.

1. Commit your changes to `main`.
2. Tag the version and push the tag:
   ```bash
   git tag v1.1.0
   git push origin v1.1.0
   ```
3. The workflow builds the installer and the portable zip and publishes the GitHub release. The release notes are generated from commits since the previous tag.
4. Installed copies see the update within a day, or right away through **Check for Updates…**.

The version number comes from the tag; you don't need to edit `Pylon.csproj`.

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
