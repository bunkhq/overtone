# Overtone for macOS

Menu bar app, Swift 6 / AppKit `NSPanel` hosting SwiftUI. Requires macOS 14 or later.

## Build

```
scripts/build-macos.sh            # release build → apps/macos/build/Overtone.app
scripts/build-macos.sh --debug    # debug core and app
open apps/macos/build/Overtone.app
```

The script runs three steps, which you can also run on their own:

| Step | Script | Output (gitignored) |
|---|---|---|
| Rust core + Swift bindings (UniFFI) | `scripts/build-core-macos.sh` | `Frameworks/overtone_coreFFI.xcframework`, `Sources/OvertoneCore/Generated/` |
| Now Playing adapter (pinned revision) | `scripts/build-adapter-macos.sh` | `build/adapter/` |
| App bundle + signature | `scripts/build-macos.sh` | `build/Overtone.app` |

Use `--skip-core` and `--skip-adapter` to rebuild only the Swift side. Run the core step again after changing anything exported from `core/src/ffi.rs`.

## Develop

After the core step has run once, `Package.swift` opens in Xcode, and these work from the terminal:

```
swift build --package-path apps/macos
swift test  --package-path apps/macos
swift run   --package-path apps/macos Overtone   # finds the adapter in build/adapter
```

Outside the bundle, "Open at Login" is disabled. Everything else works.

## Layout

```
Sources/Overtone/            main.swift (entry point only)
Sources/OvertoneMac/
  App/                       AppDelegate (menu bar menu), HotKeys, MenuBarIcon
  NowPlaying/                NowPlayingSource protocol, mediaremote-adapter source, Spotify/Music fallback
  Model/                     NowPlayingModel (debounce, clock, lyric timer), artwork → palette, Settings
  Overlay/                   OverlayPanel + snap geometry, OverlayController, SwiftUI views, ProgressBar
  Support/                   OKLCH maths for the palette cross-fade
Sources/OvertoneCore/        generated bindings to the Rust core
Resources/                   Info.plist, AppIcon.icns (scripts/gen-icon-macos.sh)
```

## Shortcuts

| Shortcut | Action |
|---|---|
| ⌃⌥⌘O | Show or hide the overlay |
| ⌃⌥⌘G | Ghost mode (dimmed, clicks pass through) |
| ⌃⌥⌘L | Next layout (pill, card, lyrics); double-clicking the overlay does the same |

Drag the overlay to move it (it snaps to the nearest edge or corner). Drag an edge or corner to resize it. Each layout keeps its own size, and Layout ▸ Reset Size restores the default.

## Lyrics

Scroll the lyric list with the wheel or trackpad to read ahead or back; it glides back to the playing line 2.5 s after you stop. Click a line to jump the song to it (synced lyrics, players with a timeline).

## Discord status

Same card as on Windows (see `docs/DISCORD.md`): "Listening to {artist}", and on the card the title, artist, the current lyric, the cover, the player's icon and a progress bar. It only shows while music plays. Turn it on with **Show in Discord Status** in the menu bar menu; it uses Overtone's built-in Discord application, so there's nothing to set up.

Overtone talks to the Discord desktop app over its local socket (`$TMPDIR/discord-ipc-N`) and retries every 20 s while Discord isn't running. Discord accepts about one update every 4 s, so lyric lines shorter than that are skipped on the card. Each update is built when it's sent, so the lyric and the progress bar are always the ones playing at that moment.

For development, `OVERTONE_DISCORD_SOCKET=/path/to/socket` points the app at a fake Discord instead.

## Signing

The script signs ad hoc by default, which is enough to run on the Mac that built it. For distribution:

```
CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" scripts/build-macos.sh --universal
```

That enables the hardened runtime. Notarisation (`xcrun notarytool submit`) is not scripted yet. `--universal` needs `rustup target add x86_64-apple-darwin`.

## Now Playing source

Since macOS 15.4, third-party apps can't read `MediaRemote.framework` directly. [`mediaremote-adapter`](https://github.com/ungive/mediaremote-adapter) (BSD-3-Clause) works around this by running under `/usr/bin/perl`, which still holds the entitlement. On launch Overtone runs the adapter's self-test. If the test passes, it keeps one `stream` process running and gets changes pushed to it, with no polling. If the test fails, it falls back to Spotify's and Music's distributed notifications (see `docs/COMPATIBILITY.md`).

The stream's PID is recorded in `~/Library/Caches/com.bunkhq.overtone/`. If Overtone is killed, the next launch stops the orphaned stream, because the adapter doesn't notice when its parent exits.
