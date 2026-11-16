# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

<!-- Desktop, not mobile: macOS + Windows, each shell native to its OS. "adaptive" here means one shared design language rendered with each OS's own windowing, tray/menu-bar and accessibility conventions. iOS/Android references do not apply. -->

## Stack

Fully native, two shells (user's choice):

- macOS: Swift, AppKit `NSPanel` hosting SwiftUI, Core Animation.
- Windows: C# / WinUI 3 (Windows App SDK) with the Composition layer.
- Shared logic (lyrics, palette, sync clock, caching): a small Rust core, exposed to both shells through a C ABI, so the hard logic is written once. See `docs/ARCHITECTURE.md`.

## Users

Primary: the author, at their own desktop, wanting an always-available, compact now-playing mini-player with synced lyrics that floats over whatever they are doing (coding, browsing, gaming). Not built for streamers or OBS.

Later: the public, once the repo is open-sourced. No release, distribution or onboarding commitments yet.

## Product Purpose

A background app living in the system tray / menu bar that shows what is playing, from any music player, as a small floating overlay whose colours are taken from the current album artwork, with time-synced lyrics. It replaces a player-specific mini-player (like Spotify's) with one that is player-agnostic, lighter, and better looking. Success: it is always running, costs almost nothing in CPU/RAM, and the author forgets it is a separate app.

## Positioning

Player-agnostic by design: it reads the operating system's own now-playing session (Windows SMTC, macOS Now Playing) rather than any one service's API, so any player that reports to the OS works without per-service integration. The overlay's colour is derived per track from the artwork, so it never looks like a generic widget.

## Operating Context

Runs at login, idle in the tray/menu bar. The overlay sits above other windows, can be dragged and snapped to screen edges, collapses when not needed, and must never steal focus or block clicks it does not need. Used across light and dark desktops, full-screen apps, multiple monitors and (on macOS) multiple Spaces.

## Capabilities and Constraints

Confirmed:
- System tray (Windows) / menu bar (macOS) presence; background operation.
- Compact floating overlay(s), more compact than the reference stream widgets.
- Time-synced lyrics.
- Palette extracted from album artwork drives the overlay's colours.
- Supports "basically any music platform".

Constraints:
- Lightweight: low idle CPU, small memory footprint, no constant polling where the OS pushes events.
- macOS 15.4+ restricts the private MediaRemote framework for third-party apps; the macOS source needs a workaround (see architecture doc).

Open:
- Product name and app icon (being decided).
- Exact set of overlay layouts beyond the compact player + lyrics line.
- Lyrics providers beyond LRCLIB, and offline/caching policy.
- Licence for the open-source release.

## Brand Commitments

Binding visual reference from the user: the "now playing" stream widget style in the attached reference (album-art-tinted cards, soft blur, rounded art, slim progress bars, vinyl variant). The colour-from-artwork mechanism is the core of the design language. Name and icon are open. Keep third-party platform names (Spotify, Apple Music, etc.) out of the product name.

## Evidence on Hand

- Reference video: `C:\Users\chris\.t3\userdata\attachments\897402e3-...mp4` (widget styles, 6 s).
- Reference widget catalogue: nuttylmao's widget page on Notion (user-supplied link).
- No users, testimonials, benchmarks or press exist. Do not invent any.

## Product Principles

1. Ambient first: glanceable in under a second, invisible when not wanted, never interrupts.
2. The music sets the colour: every track should make the overlay feel newly made for it.
3. One source of truth: read the OS now-playing session, not a pile of service integrations.
4. Weightless: motion is soft and physical, resource use is near zero.
5. Native everywhere: each OS gets its own windowing, tray and accessibility behaviour.

## Accessibility & Inclusion

Respect OS reduce-motion and reduce-transparency settings. Lyrics and title text must stay legible on any extracted palette (contrast-checked, not assumed). Full keyboard and screen-reader access to the tray/menu-bar menu and settings.
