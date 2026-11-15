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
