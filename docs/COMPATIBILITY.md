# Player compatibility

Overtone reads the operating system's now-playing session instead of integrating with players one by one. On Windows that session is SMTC (System Media Transport Controls, read through `GlobalSystemMediaTransportControlsSessionManager`). Every app on the community-tested list below is compatible **because it reports to SMTC**, so the same single integration covers all of them.

Source: the community compatibility list linked from nutty's now-playing widgets (Notion export, tested on Windows). Overtone has not re-tested each entry yet; the "Verified" column fills in as we do.

## Works out of the box

| App | Type | Art quality | Progress | Notes | Verified |
|---|---|---|---|---|---|
| Spotify | Streaming | High | Yes | | |
| Apple Music | Streaming | High | Yes | | |
| Apple Music (Cider) | Streaming | High | Yes | | |
| TIDAL | Streaming | Low | Yes | | |
| Deezer | Streaming | Low | Yes | | |
| YouTube Music (web) | Streaming | Low | Yes | | |
| YouTube Music (Pear Desktop) | Streaming | Low | Yes | Third-party client | |
| YouTube Music (YTMDesktop) | Streaming | Low | Yes | Third-party client | |
| SoundCloud | Streaming | Low | Yes | Web player tested | |
| Amazon Music | Streaming | Low | Yes | Web player only. Desktop app sends title only (no artist, no progress) | |
| Pretzel | Streaming | Low | Yes | Web player only | |
| Nintendo Music | Streaming | Low | Yes | Web player. Sends no artist (game name), title only | |
| Chrome, Edge, Firefox, Opera, Brave | Browser | Low | Yes | Any site using the Media Session API | |
| Windows Media Player (new) | Local | High | Yes | | |
| mpv | Local | High | Yes | | |
| MPC-HC | Local | High | No | | |

## Works with a player-side plugin

The plugin makes the player report to SMTC; Overtone needs nothing extra.

| App | Art | Progress | Plugin |
|---|---|---|---|
| foobar2000 | High | Yes | [foo_mediacontrol](https://github.com/dumbie/foo_mediacontrol) |
| VLC | High | Yes | [vlc-win10smtc](https://github.com/spmn/vlc-win10smtc); progress is broken there, use the [fork](https://github.com/robwiz9/vlc-win10smtc) |
| MusicBee | High | No | SMTC plugin from the MusicBee forum |
| Winamp | High | No | [gen_smtcinterop](https://github.com/laszlolukacs/gen_smtcinterop) |
| AIMP | High | No | SMTC plugin from the AIMP catalogue |
| Qobuz | High | Yes | [qobuz-smtc](https://github.com/TubaApollo/qobuz-smtc) |
| iTunes | High | Yes | SMTC companion app from the Microsoft Store |

## Not compatible

| App | Why |
|---|---|
| Pandora | Doesn't report to SMTC |
| Epidemic Sound | Reports, but every track is titled "Epidemic Sound" with no art |
| Windows Media Player (Legacy) | Doesn't report to SMTC |
| Dopamine | Doesn't report to SMTC |

## What this means for the app

The list exposes four gaps the shell and core must handle rather than ignore:

1. **Low-resolution artwork** (every browser and most web players). The palette engine only needs 64×64, so colours are fine, but the displayed cover looks soft. Plan: an optional *artwork upgrade* step in the core that looks the track up (iTunes Search API, free, no key) and swaps in a 600×600 cover when the match is confident (artist, title, and duration within ±2 s). Off by default until it's reliable; never blocks the first render.
2. **Missing fields.** Some sources send a title with no artist (Nintendo Music, Amazon's desktop app) or no timeline. The overlay must lay out cleanly with only a title, hide the progress bar when there's no timeline, and skip the lyrics lookup when there's no artist.
3. **No progress support** (MPC-HC, MusicBee, Winamp, AIMP). The progress bar hides, and lyrics fall back to unsynced (plain) display, because synced lyrics need a position.
4. **Several sessions at once** (e.g. Spotify plus a YouTube tab). Default to the session that is actually playing, then the system's "current" session. Let the user pin a source app or ignore apps (by `SourceAppUserModelId`) from the tray menu.

macOS has no equivalent list yet. The macOS source (Now Playing via `mediaremote-adapter`) covers whatever appears in Control Centre's Now Playing, which includes Spotify, Music, and browsers playing media; the list gets built as we test.
