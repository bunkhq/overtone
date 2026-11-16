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
│ Core (Rust, one codebase, exposed through a small C ABI)               │
│  palette  ─ OKLab k-means + contrast solver → Palette{base,accent,ink} │
│  lyrics   ─ providers (LRCLIB) · LRC parser · track matcher · cache    │
│  clock    ─ playback clock model (position interpolation, seeks)       │
│  store    ─ on-disk cache (lyrics, palettes keyed by artwork hash)     │
└────────────────────────────────────────────────────────────────────────┘
```

Rule: **the shell owns anything the OS draws or reports; the core owns anything that is maths, text or network.** Keeping the core free of UI means both shells render identical palettes and identical lyric timing.

### Why a Rust core instead of writing it twice

The shared logic is small (~2k lines) but fiddly: palette scoring, LRC edge cases, fuzzy track matching, cache invalidation. Written twice, the two apps drift and every bug gets fixed twice. The binding is a deliberately small C ABI (`core/src/ffi.rs`): plain structs for the hot paths (palette, backdrop, clock, OKLCH mix) and a JSON string for lyrics. C# calls it with P/Invoke (`apps/windows/Overtone/Core/CoreLib.cs`); Swift can call the same functions through a bridging header. Cost: a cargo step in each build (the Windows `.csproj` runs it and copies `overtone_core.dll`). [UniFFI](https://github.com/mozilla/uniffi-rs) stays an option if the surface grows, but at about a dozen functions a hand-written ABI is less machinery.

## 3. Repository layout

```
/core                    Rust crate: palette, lyrics, clock, store (+ C ABI in src/ffi.rs)
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
  gen-ico.py             icon masters → .ico (app + tray)
/docs
```

## 4. Data flow for one track change

1. OS fires the change event (`MediaPropertiesChanged` on Windows, adapter JSON line on macOS).
2. Shell **debounces 150 ms** (players often fire title, then artwork, then timeline as separate events) and builds a `Track { title, artist, album, duration, artwork_rgba, source_app }`.
3. Shell calls `core.palette_for(artwork)`. Cached by a hash of the artwork, so repeat tracks cost nothing. Target under 5 ms on a 64×64 downsample.
4. Shell calls `core.lyrics_for(track)` asynchronously. The overlay renders immediately without lyrics, then fades lyrics in when they arrive (or never shows a lyrics row if none exist).
5. The overlay **cross-fades the palette in OKLCH over ~600 ms** while the artwork swaps. Colour moving through OKLCH avoids the muddy grey midpoint you get blending in RGB.

## 5. Staying lightweight

The app should idle near **0% CPU** and stay small in memory. The rules that get it there:

- **Event-driven, never polled.** Both OS sources push events. No timers checking "what's playing".
- **Interpolate time locally.** SMTC and Now Playing report `position` + the timestamp it was true at. The clock is `position + (now − updatedAt) × rate` while playing. Spotify on Windows only updates its timeline every few seconds, so this is required, not an optimisation.
- **Progress bar, per OS.** On macOS, start one `CABasicAnimation` from the current fraction to 1.0 over the remaining duration on each play/seek/pause; Core Animation renders it out of process. **On Windows this does not hold:** WinUI 3's compositor runs *inside the app*, so any running animation renders every frame in our process (a continuous progress animation measured about 8–12% of a core). The Windows shell instead steps the fill whenever it would move about one physical pixel (every 0.25–1 s), one frame per step.
- **No looping animations while idle.** The instrumental-gap dots breathe only when the lyric panel is on screen, the gap is the current line and the music is playing. A loop left running on a collapsed panel cost about 8% of a core.
- **Frequent moves are short.** Lyric changes happen every few seconds, so they glide for 340 ms instead of springing; springs are kept for rare moments (snap, mode change).
- **Lyrics advance on a single timer**, scheduled for the next line's timestamp, rescheduled on seek. No per-frame loop. Only the rows around the three-line window stay in the render tree.
- **No frame loop at all when hidden, paused, or fully occluded.**
- **Artwork blur is done once per track**, at low resolution, into a cached bitmap, not as a live blur over the desktop.
- Lazy-load settings UI. The tray + overlay are the only always-loaded surfaces.

Measured on Windows (Release, x64, 60 s windows): paused or hidden, about 0.1% of one core; playing, 1.5–3.6% depending on mode, almost all of it frames during lyric transitions. Memory is about 100 MB private / 155 MB working set. That is the WinUI 3 + .NET runtime baseline and well above the original 40 MB hope; trimming it (trimming, NativeAOT, fewer WinUI assemblies) is a later task.

## 6. Making it feel floaty (window behaviour)

| | macOS | Windows |
|---|---|---|
| Window | `NSPanel`, `.nonactivatingPanel` + `.borderless` | `AppWindow` with `WS_EX_TOOLWINDOW` (no taskbar button) + `WS_EX_NOACTIVATE`. The presenter keeps `WS_DLGFRAME` even with border and title bar off, so a window subclass answers `WM_NCCALCSIZE` with 0 (no frame; the client is the whole window) |
| Always on top | `level = .floating` | `IsAlwaysOnTop` alone does not stick on a window that was hidden when it was set; the subclass rewrites every z-order change in `WM_WINDOWPOSCHANGING` to `HWND_TOPMOST` |
| Follows you | `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]` | Remembers per-monitor position; re-places on display change |
| Transparency | `isOpaque = false`, `backgroundColor = .clear`, SwiftUI content | `TransparentTintBackdrop` (WinUIEx). The card's shadow is a pre-rendered nine-grid (`scripts/gen-shadow.py`: ambient + contact Gaussian layers) that fades to nothing inside a transparent margin (20 px sides, 14 above, 26 below). Composition `DropShadow` was dropped: a `LayerVisual` clips the blur to its bounds (a hard edge), and growing the layer turned the window opaque. The margin doubles as the resize grab zone |
| Never steals focus | non-activating panel | `WS_EX_NOACTIVATE`, `AppWindow.Show(false)`, `WM_MOUSEACTIVATE` answered with `MA_NOACTIVATE` |
| Ghost mode | `ignoresMouseEvents = true` | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` |
| No Dock / taskbar icon | `LSUIElement = YES` | tool window + tray only |

Interaction model:

- **Drag anywhere** to move; on release it **springs to the nearest edge or corner** with a 12–16 px margin (magnetic snap).
- **Tuck**: drag past a screen edge and it collapses into a slim peek tab showing only the artwork's accent colour. Hover the tab to slide it back out.
- **Fixed footprint**: no overlay changes size under the pointer, and controls are always present. Switching between pill, card and lyric panel is an explicit choice (tray menu, double-click, dragging the top or bottom edge, or hotkey), never a hover side effect.
- **Resize snaps to modes**: dragging the top or bottom edge proposes a height; the card morphs to whichever mode is nearest (with hysteresis), holding the opposite edge still.
- **Mode morph (Windows)**: the window grows once to hold both sizes, then every frame moves to the card's interpolated top-left while the card's size animates inside it, so the card's offset in the window never changes and nothing jumps; at the end the window shrinks from its right and bottom only. Each layout has a fixed size (no reflow mid-morph): the old one fades out, the new one fades in as the card arrives. Measured at the display's full rate (10 ms frames, none dropped).
- **Lyric text**: in the lyric panel lines wrap (up to three lines; rows have measured heights). In the pill a line wider than the pill holds for a beat, then glides sideways to its end before the next line is due.
- **Hover**: transport buttons show a wash plus a white shine that follows the cursor. On Windows a never-activated window doesn't reliably get pointer-leave, so a watchdog clears hover once the cursor is off the window.
- **Background** (tray menu): blurred cover under a palette scrim; gradient (the base warming toward the accent in the top-left corner, lightness held so inks keep contrast); or solid base colour. The OS transparency switch turns the blurred cover into solid.
- **Tone** (tray menu): match the cover (light only for pale covers), dark, or light.
- **Highlight**: the title, the current lyric (lyric panel and pill), the progress fill and the transport glyphs all take the palette's highlight (`accent`), so the whole card reads as one colour story. The unfilled track is a grey wash on a dark card and a paler band than the ground on a light one.
- **Native Windows look** (tray toggle, on by default): Windows draws the frame (DWM shadow, 1 px border, standard 8 px rounded corners; the window is exactly the card, no shadow margin), the background is the system Acrylic material tinted with the palette's base colour (driven through `DesktopAcrylicController` with the window reported as always active, since the overlay never takes focus), transport glyphs are Segoe Fluent Icons, hover is a plain fill without the cursor-following shine, and artwork uses Windows 11 corner radii (4-6 px). Size animations resize the window itself each frame. Off: Overtone's drawn card (palette surface, drawn shadow, pill shapes, drawn glyphs); the Background menu applies only then.
- **Free sizing**: drag any edge or corner; there are no snap sizes. The card's height picks the layout: a pill below 124 px (from 68 px it adds the song above the lyric; from 84 px it becomes a tall pill: artwork fills the height, the lyric is set larger, and the controls move under the text beside the progress bar so the text gets the full width), the player card below 200 px (artwork grows with height, plus a line for the current lyric, which glides across when it is too long, like the pill's), the lyric panel above (more lines as it grows). Width reflows: lyric rows re-wrap. Crossing a threshold cross-fades the layouts. Double-click cycles size presets (400x48, 340x150, 360x232), animated. A scrolled line re-aligns and re-times its glide when the width changes. Size and position persist.
- **No lyrics, no space**: when a song has no lyrics (not found, instrumental, error), the lyric panel folds down to its mini-player row and the player card drops its lyric line; the window springs to the new size from the corner nearest the screen edges, and unfolds when the next song has lyrics. While lyrics load the previous state holds (no flicker); while the user resizes, the panel shows at full size.
- **Music only** (tray toggle, on by default): an item counts as music if it comes from a dedicated music player, or carries an album and artist (YouTube Music, Spotify, Deezer on the web), and isn't marked video by the OS; anything else must be a song iTunes or Deezer knows (an official music video passes, a random video doesn't; offline gives the benefit of the doubt). Sessions judged not music don't compete for the overlay until their item changes.
- **Seeking**: the progress bars are sliders (hover thickens, press or drag scrubs with the fill and elapsed time following, release seeks), and synced lyric lines are links to their timestamps (hover wash, click to seek). The overlay updates at once and the player confirms with a timeline event. Disabled when the session reports no position control.
- **Free lyric scrolling**: wheel or touchpad scrolls through the whole song; 2.5 s after the last scroll the panel glides back to the playing line. The highlight keeps moving meanwhile.
- **Staying in sync**: the core clock adopts every timeline correction (only large ones re-anchor animations), the lyric timer is rescheduled on every report, the timeline is re-read every 2 s while playing, and a resume whose timestamp predates it (Spotify doesn't rewrite the timeline on resume) is treated as valid from the resume moment.
- **Discord Rich Presence**: see docs/DISCORD.md.
- **Lyric panel scroll**: the previous line sits whole at the top, the current line under it, and the following lines fill the rest; the row cut by the bottom edge fades out (a composition mask: the column renders into a `CompositionVisualSurface` painted through a vertical alpha gradient, since XAML has no opacity mask). If the previous and current lines don't both fit, the current line goes to the top. At song start the first line is at the top; at the end the last line rests on the bottom edge. Each edge fades only while a row is actually cut by it.
- **Ghost mode** (hotkey): the overlay dims to ~55% opacity and lets clicks pass through. Since a click-through window can't receive hover, exit ghost mode via the hotkey or the tray menu.
- **Full-screen apps**: optional auto-hide when a full-screen window owns the display.
- Respect **Reduce Motion** (swap springs for 150 ms fades) and **Reduce Transparency** (solid palette fill, no blurred art).

Motion is springs, not easing curves. One spring family everywhere, defined in `tokens.json`, so both shells move identically:

| Token | Use | Response / damping |
|---|---|---|
| `spring.float` | position snap, tuck | 0.55 s / 0.78 |
| `spring.resize` | mode switch: the card's size animates continuously between modes | 0.34 s / 0.90 |
| `spring.morph` | artwork lift on track change, tuck | 0.42 s / 0.86 |
| `glide.lyric` | lyric line slide-up, pill line swap | 340 ms ease-out (frequent, so not a spring) |
| `fade.palette` | palette cross-fade | 600 ms, OKLCH interpolation |

SwiftUI's `.spring(response:dampingFraction:)` takes these directly; on Windows, Composition's `SpringVector3NaturalMotionAnimation` takes `Period` and `DampingRatio`, so convert response to period once in `gen-tokens`.

## 7. Palette engine (core/palette)

1. Downsample artwork to 64×64, drop near-transparent pixels.
2. Convert to **OKLab**, run k-means with k = 6 (seeded by median cut so results are deterministic per image).
3. Score clusters by `population × (0.35 + chroma)`; ignore clusters smaller than 3%. Then run a second small k-means twice: over the most chromatic 5% of all pixels, and over the most chromatic pixels whose hue differs from the dominant by more than ~35°, so a small contrasting detail (a red dot on a pale sleeve, pink lights on a blue cover) still counts instead of being crowded out by a large area of strong dominant colour.
4. Pick roles:
   Two colours come off the cover, as on a printed sleeve: the **dominant** (the largest cluster, nudged toward colourful ones) and the **vivid detail** (the most chromatic cluster in a contrasting hue, falling back to a much stronger version of the dominant).
   - Dark card: `base` is a near-neutral ground carrying a trace of the dominant hue (L 0.20–0.25, chroma ≤ 0.025); `accent` (the highlight) is the dominant as a pastel (L ≈ 0.90, chroma 0.035–0.07). A grey dominant borrows the vivid detail's hue.
   - Light card: `base` is the dominant itself as a pale ground (L 0.91, chroma ≤ 0.06); `accent` is the vivid detail deepened (L ≈ 0.38, chroma 0.10–0.16), like coloured type on a pale sleeve.
   - `accent` walks its lightness away from `base` until it holds 4.5:1, because it sets text (title, current lyric).
   - `ink`, `inkSoft`, `inkFaint`: text colours tinted toward `base`'s hue, solved against `base` by moving only lightness: `ink` ≥ 7:1, `inkSoft` ≥ 4.5:1 (artist, times; solved to 4.6 for headroom on the gradient's tinted corner), `inkFaint` ≥ 3:1 (dimmed previous/next lyric lines, which are large text).
   - The gradient background's corner tint keeps the base's WCAG luminance exactly (lightness is bisected for the tinted hue), so every ink keeps its contrast there.
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

- ~~**Tray icon follows the music.**~~ Done on Windows: the tray icon redraws the three waves per track in colours from the palette (`tray_colors` in the core: the highlight first, then the most different hues among the cover's main colours, made vivid and lit for the taskbar theme, at least 3:1 on dark or light taskbars). No artwork or nothing playing: the brand icon. macOS keeps a monochrome template, as the menu bar requires.
- **Artwork upgrade** for low-resolution sources; see `docs/COMPATIBILITY.md`.

## 10. Build order

1. ~~**Core skeleton**: palette + LRC parser + clock, with tests. No UI.~~ Done: `core/` (palette, LRC, clock, matcher, LRCLIB provider, OKLCH mix, C ABI; 39 tests). Still to add: SQLite cache.
2. ~~**Windows source spike**~~ Done: `apps/windows/tools/SmtcProbe` prints every SMTC session and streams changes (`dotnet run`). Verified with Spotify; the rest of `docs/COMPATIBILITY.md` gets ticked off as tested.
3. **macOS source spike**: the same with `mediaremote-adapter`, on the newest macOS available.
4. ~~**One overlay, one OS**~~ Done on Windows: `apps/windows/Overtone`. Pill, player card and lyric panel; palette and blurred cover per track with an OKLCH cross-fade; synced lyrics; drag with magnetic edge snap; mode changes anchored to the nearest corner; hides itself when nothing plays; tray icon with show/hide, modes and quit; follows the OS animation and transparency switches.
5. Settings window, launch at login, global hotkeys, ghost mode, tuck, re-placement on display change, the coloured tray icon (§9).
6. Second OS shell, reaching parity against the same tokens and core.
7. Signing and notarisation (macOS) and MSIX or installer signing (Windows), then the open-source release.
