# Architecture

Name: **Overtone**. See `design/concept.html` for the brand and `docs/COMPATIBILITY.md` for the player list.

A tray / menu-bar app that reads whatever is playing from the OS, colours a small floating overlay from the album artwork, and shows synced lyrics. Two native shells, one shared core.

## 1. The one decision that makes "any platform" work

Do **not** integrate music services one by one. Read the operating system's now-playing session instead. Every serious player already reports to it, because that's what drives the OS media keys, lock screen and volume flyout.

| OS | Source | What it gives | Works with |
|---|---|---|---|
| Windows 10/11 | `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (SMTC / GSMTC) | Title, artist, album, artwork stream, playback status, timeline (position + `LastUpdatedTime`), transport controls, **push events** | Spotify, Apple Music, Tidal, Deezer, Amazon Music, foobar2000, MusicBee, and any browser tab using the Media Session API (YouTube Music, SoundCloud, Bandcamp, web Spotify…) |
| macOS | Now Playing (MediaRemote) via [`mediaremote-adapter`](https://github.com/ungive/mediaremote-adapter) | The same fields, streamed as JSON, push-based | Same list: anything that shows up in Control Centre's Now Playing |
| macOS fallback | `DistributedNotificationCenter` (`com.spotify.client.PlaybackStateChanged`, `com.apple.Music.playerInfo`) + ScriptingBridge for artwork/position | Push events from Spotify.app and Music.app only | Used only if the adapter breaks after an OS update |

**macOS caveat:** since macOS 15.4, Apple blocks third-party processes from reading the private `MediaRemote.framework` directly. `mediaremote-adapter` works around this by running its loader through `/usr/bin/perl`, which still holds the needed entitlement. Treat it as the primary path but keep the source behind an interface (`NowPlayingSource`), because Apple can close this at any time. The notification fallback keeps Spotify and Apple Music working if it does.

Per-service APIs (Spotify Web API, etc.) are **not** needed for the core. Add them later only for extras the OS doesn't expose, like "like this track".

## 2. Layering

```
┌──────────────────────── Shell (native, per OS) ────────────────────────┐
│ Tray / menu bar · Overlay window(s) · Animation · Settings · Hotkeys   │
│ NowPlayingSource (SMTC | mediaremote-adapter)  · Artwork decode → RGBA │
└───────────────▲───────────────────────────────────────┬────────────────┘
                │ events: TrackChanged, PlaybackChanged │ calls
┌───────────────┴───────────────────────────────────────▼────────────────┐
│ Core (Rust, one codebase, bound via UniFFI)                            │
│  palette  ─ OKLab k-means + contrast solver → Palette{base,accent,ink} │
│  lyrics   ─ providers (LRCLIB) · LRC parser · track matcher · cache    │
│  clock    ─ playback clock model (position interpolation, seeks)       │
│  store    ─ on-disk cache (lyrics, palettes keyed by artwork hash)     │
└────────────────────────────────────────────────────────────────────────┘
```

Rule: **the shell owns anything the OS draws or reports; the core owns anything that is maths, text or network.** Keeping the core free of UI means both shells render identical palettes and identical lyric timing.

### Why a Rust core instead of writing it twice

The shared logic is small (~2k lines) but fiddly: palette scoring, LRC edge cases, fuzzy track matching, cache invalidation. Written twice, the two apps drift and every bug gets fixed twice. [UniFFI](https://github.com/mozilla/uniffi-rs) generates Swift bindings officially and C# bindings through [`uniffi-bindgen-cs`](https://github.com/NordSecurity/uniffi-bindgen-cs). Cost: a cargo step in each build (an `.xcframework` for macOS, a `.dll` for Windows). If that turns out heavier than it's worth, the core's API is small enough to port to Swift and C# by hand, so it's a cheap decision to reverse.

## 3. Repository layout

```
/core                    Rust crate: palette, lyrics, clock, store (+ UniFFI exports)
  /src
  /tests                 golden tests: sample artworks → expected palettes, LRC fixtures
/apps
  /macos                 Xcode project, Swift. AppKit NSPanel + SwiftUI views
  /windows               WinUI 3 solution, C#. Windows App SDK + Composition
/design
  tokens.json            spacing, radii, type scale, motion springs (single source)
  concept.html           visual concept: overlay modes, palette engine, brand
  /icon                  icon masters (SVG), exported .icns / .ico
/scripts
  gen-tokens             tokens.json → Tokens.swift + Tokens.cs
  build-core             cargo build + uniffi bindgen for both targets
/docs
```

## 4. Data flow for one track change

1. OS fires the change event (`MediaPropertiesChanged` on Windows, adapter JSON line on macOS).
2. Shell **debounces 150 ms** (players often fire title, then artwork, then timeline as separate events) and builds a `Track { title, artist, album, duration, artwork_rgba, source_app }`.
3. Shell calls `core.palette_for(artwork)`. Cached by a hash of the artwork, so repeat tracks cost nothing. Target under 5 ms on a 64×64 downsample.
4. Shell calls `core.lyrics_for(track)` asynchronously. The overlay renders immediately without lyrics, then fades lyrics in when they arrive (or never shows a lyrics row if none exist).
5. The overlay **cross-fades the palette in OKLCH over ~600 ms** while the artwork swaps. Colour moving through OKLCH avoids the muddy grey midpoint you get blending in RGB.

## 5. Staying lightweight

The app should idle near **0% CPU and under ~40 MB RAM**. The rules that get it there:

- **Event-driven, never polled.** Both OS sources push events. No timers checking "what's playing".
- **Interpolate time locally.** SMTC and Now Playing report `position` + the timestamp it was true at. The clock is `position + (now − updatedAt) × rate` while playing. Spotify on Windows only updates its timeline every few seconds, so this is required, not an optimisation.
- **Let the compositor animate the progress bar.** On each play/seek/pause, start one linear animation from the current fraction to 1.0 over the remaining duration (`CABasicAnimation` on macOS, `ScalarKeyFrameAnimation` on Windows Composition). It then runs on the system compositor at display refresh with zero app CPU per frame.
- **Lyrics advance on a single timer**, scheduled for the next line's timestamp, rescheduled on seek. No per-frame loop.
- **No frame loop at all when hidden, paused, or fully occluded.**
- **Artwork blur is done once per track**, at low resolution, into a cached bitmap, not as a live blur over the desktop.
- Lazy-load settings UI. The tray + overlay are the only always-loaded surfaces.

## 6. Making it feel floaty (window behaviour)

| | macOS | Windows |
|---|---|---|
| Window | `NSPanel`, `.nonactivatingPanel` + `.borderless` | Borderless `AppWindow`, `WS_EX_TOOLWINDOW` (no taskbar button) + `WS_EX_NOACTIVATE` |
| Always on top | `level = .floating` | `OverlappedPresenter.IsAlwaysOnTop = true` |
| Follows you | `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]` | Remembers per-monitor position; re-places on display change |
| Transparency | `isOpaque = false`, `backgroundColor = .clear`, SwiftUI content | `TransparentTintBackdrop` (WinUIEx) or custom `SystemBackdrop`; content in Composition visuals |
| Never steals focus | non-activating panel | `WS_EX_NOACTIVATE`, `ShowWindow(SW_SHOWNOACTIVATE)` |
| Ghost mode | `ignoresMouseEvents = true` | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` |
| No Dock / taskbar icon | `LSUIElement = YES` | tool window + tray only |

Interaction model:

- **Drag anywhere** to move; on release it **springs to the nearest edge or corner** with a 12–16 px margin (magnetic snap).
- **Tuck**: drag past a screen edge and it collapses into a slim peek tab showing only the artwork's accent colour. Hover the tab to slide it back out.
- **Fixed footprint**: no overlay changes size under the pointer, and controls are always present. Switching between pill, card and lyric panel is an explicit choice (tray menu, double-click, or hotkey), never a hover side effect.
- **Lyric panel scroll**: the first line is pinned to the top slot, then the current line stays in the middle slot, and the last line is pinned to the bottom, so the panel never shows empty rows.
- **Ghost mode** (hotkey): the overlay dims to ~55% opacity and lets clicks pass through. Since a click-through window can't receive hover, exit ghost mode via the hotkey or the tray menu.
- **Full-screen apps**: optional auto-hide when a full-screen window owns the display.
- Respect **Reduce Motion** (swap springs for 150 ms fades) and **Reduce Transparency** (solid palette fill, no blurred art).

Motion is springs, not easing curves. One spring family everywhere, defined in `tokens.json`, so both shells move identically:

| Token | Use | Response / damping |
|---|---|---|
| `spring.float` | position snap, tuck | 0.55 s / 0.78 |
| `spring.morph` | explicit mode switch (pill ↔ card ↔ lyrics), tuck | 0.42 s / 0.86 |
| `spring.lyric` | lyric line slide-up | 0.50 s / 0.92 |
| `fade.palette` | palette cross-fade | 600 ms, OKLCH interpolation |

SwiftUI's `.spring(response:dampingFraction:)` takes these directly; on Windows, Composition's `SpringVector3NaturalMotionAnimation` takes `Period` and `DampingRatio`, so convert response to period once in `gen-tokens`.

## 7. Palette engine (core/palette)

1. Downsample artwork to 64×64, drop near-transparent pixels.
2. Convert to **OKLab**, run k-means with k = 6 (seeded by median cut so results are deterministic per image).
3. Score clusters by `population × (0.35 + chroma)`; ignore clusters smaller than 3%. Then run a second small k-means over only the most chromatic 5% of pixels, so a small vivid detail (a red dot on a pale cover, one neon stripe) can still win `accent` instead of being averaged into a muddy cluster.
4. Pick roles:
   - `base`: the dominant cluster, its OKLCH lightness clamped to 0.18–0.28 for a dark card or 0.90–0.95 for a light card.
   - `accent`: the most chromatic cluster whose hue differs from `base` by more than 25°, or a more saturated shift of `base` if none does. Drives the progress fill and the current lyric glow.
   - `ink`, `inkSoft`, `inkFaint`: text colours tinted toward `base`'s hue, solved against `base` by moving only lightness: `ink` ≥ 7:1 (title, current lyric), `inkSoft` ≥ 4.5:1 (artist, times), `inkFaint` ≥ 3:1 (dimmed previous/next lyric lines, which are large text).
5. Pick tone: light card when the artwork's mean lightness is above 0.72 and its chroma is low (the pale card in the reference), otherwise dark. The user can force a tone.
6. Output `Palette { base, accent, ink, inkSoft, tone }` plus a 3-stop mesh for the blurred backdrop.

Golden tests: a folder of sample covers with their expected palettes, so changes to the scoring can't silently make colours worse.

## 8. Lyrics (core/lyrics)

- **Provider: [LRCLIB](https://lrclib.net)** (free, no API key, community-run, returns synced LRC). Call `GET /api/get` with artist, track, album and duration; fall back to `/api/search` and pick the result whose duration is within ±2 s. Send a `User-Agent` naming the app and its repo URL, as LRCLIB asks.
- `LyricsProvider` trait so more sources can be added. Avoid scraping services whose terms forbid it; that matters once the repo is public.
- **Matcher** normalises before lookup: strip `- Remastered 2011`, `(feat. …)`, `[Live]`, and "Topic" suffixes from YouTube channel names, and fold case and accents.
- **Cache**: one row per (normalised artist, title, duration bucket) in a small SQLite file. Cache "no lyrics found" as well, for 7 days, so missing tracks aren't refetched on every play.
- Plain (unsynced) lyrics are shown as a slow auto-scroll, or the lyrics row is hidden; the user decides in settings.
- Instrumental gaps longer than 5 s show a breathing three-dot indicator in place of the line.

## 9. Later

- **Tray icon follows the music.** On Windows, redraw the tray icon's three waves in the current track's palette (accent, base-shifted, ink) on each track change; macOS stays a monochrome template because the menu bar requires it. Generate the 16/20/24/32 px bitmaps from the same wave geometry as `scripts/gen-icon.mjs`, cached per palette.
- **Artwork upgrade** for low-resolution sources; see `docs/COMPATIBILITY.md`.

## 10. Build order

1. **Core skeleton**: palette + LRC parser + clock, with tests. No UI.
2. **Windows source spike**: console app printing SMTC events from Spotify, a browser tab and Apple Music. This proves the "any platform" claim on day one.
3. **macOS source spike**: the same with `mediaremote-adapter`, on the newest macOS available.
4. **One overlay, one OS**: compact pill + card + lyric line, palette-driven, drag and snap. Whichever OS you use day to day goes first.
5. Tray menu, settings, launch at login, ghost mode, tuck.
6. Second OS shell, reaching parity against the same tokens and core.
7. Signing and notarisation (macOS) and MSIX or installer signing (Windows), then the open-source release.
