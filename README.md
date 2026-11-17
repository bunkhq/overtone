<img src="assets/icon.svg" width="72" height="72" alt="">

# Overtone

A tray app for macOS and Windows that floats a small now-playing overlay over whatever you're doing. It takes its colours from the album cover and shows synced lyrics, and it works with any player the operating system can see: Spotify, Apple Music, TIDAL, YouTube Music, browsers, foobar2000, VLC and [many more](docs/COMPATIBILITY.md).

Status: early development.

- [Architecture](docs/ARCHITECTURE.md)
- [Player compatibility](docs/COMPATIBILITY.md)
- [Product brief](PRODUCT.md)
- [Design concept](design/concept.html) (open in a browser)

## Layout

```
core/            Rust: palette engine, lyrics, playback clock (shared by both apps)
apps/windows/    C# / WinUI 3 shell
apps/macos/      Swift / AppKit shell
design/          concept board, icon masters
docs/            architecture and compatibility
```

## Download

Get the latest build from [Releases](https://github.com/bunkhq/overtone/releases/latest):

- **macOS 14 or later** (Apple Silicon and Intel): `Overtone-…-macOS.dmg`. Open it and drag Overtone to Applications. The app is not notarised yet, so the first launch is blocked: open System Settings > Privacy & Security and click **Open Anyway**.
- **Windows 10 (2004) or later**: `Overtone-…-Windows-x64-Setup.exe`, or `…-arm64-Setup.exe` on ARM PCs. It installs for your account, no admin rights. The installer is not signed yet, so SmartScreen may warn: click **More info**, then **Run anyway**. A portable `.zip` of each is attached too.

### Updates

Overtone checks GitHub for a new release shortly after it starts and every six hours. When there is one, it appears at the top of the tray (Windows) or menu bar (macOS) menu: click **Install update** and Overtone downloads it, checks it against the release's `SHA256SUMS`, installs it and restarts, in a few seconds. **Check for updates…** in the same menu checks now. A portable or development copy, or a Mac copy in a folder you can't write to, gets **Download update…**, which opens the release page.

## Install it on Windows from source

Needs the .NET 10 SDK and a Rust toolchain with the `x86_64-pc-windows-msvc` target. From the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-windows.ps1
```

This builds a self-contained Release copy and installs it for the current user in `%LOCALAPPDATA%\Programs\Overtone`, without admin rights. It adds a Start menu shortcut and starts Overtone at sign-in; turn that off with **Start with Windows** in the tray menu, Task Manager's Startup apps, or `-NoStartup`. Overtone appears in Settings > Apps > Installed apps, where it uninstalls. Run the script again to update; settings are kept.

## Run it on Windows (development)

```sh
cd apps/windows/Overtone
dotnet build -c Release -p:Platform=x64      # also builds core/ and copies overtone_core.dll
bin/x64/Release/net10.0-windows10.0.22621.0/Overtone.exe
```

Add `-p:SkipCore=true` when only C# changed. The overlay appears when any player reports to Windows; left-click the tray icon to show or hide it. Right-click for the same menu as on macOS: what's playing with play/pause, next and previous; layout (pill, player card, lyrics); ghost mode; tone, background and native look; the coloured tray icon; music only; Discord; start with Windows; updates; About and Quit. Double-click the overlay to cycle layouts, or drag its top or bottom edge to resize it into another one. Drag it anywhere and it snaps to nearby screen edges.

Global shortcuts (Control-Option-Command on macOS):

| | |
|---|---|
| Ctrl+Alt+Shift+O | Show or hide the overlay |
| Ctrl+Alt+Shift+G | Ghost mode: dim the overlay and let clicks pass through it |
| Ctrl+Alt+Shift+L | Next layout |

Settings live in `%LOCALAPPDATA%\Overtone\settings.json`; unexpected errors are appended to `errors.log` beside it, and lyrics are cached in `Cache\lyrics`.

### Test tools

- `apps/windows/tools/SmtcProbe`: prints every media session Windows sees and streams changes.
- `apps/windows/tools/FakePlayer`: a silent, real media session for testing without touching your player:
  `dotnet run -- "Mr. Brightside" "The Killers" "Hot Fuss" 222 cover.png 30` (title, artist, album, seconds, artwork, start at).

## Build (macOS)

Needs Xcode 16+ (Swift 6), Rust, and cmake (`brew install cmake`).

```
scripts/build-macos.sh                 # core + adapter + app → apps/macos/build/Overtone.app
open apps/macos/build/Overtone.app
```

See [apps/macos/README.md](apps/macos/README.md) for development, tests and signing.
