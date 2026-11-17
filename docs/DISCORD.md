# Discord Rich Presence

Overtone can show what you're listening to on your Discord profile:

- **Member list status:** "Listening to {artist}". **Discord status shows** in the menu switches it to the song title or the current lyric (with the lyric, the artist takes the lyric's place on the card; between lines and without lyrics the status falls back to the artist). Lyric updates obey the same 4 s rate limit as the rest of the card.
- **Profile card:** the song title, the artist, and the current lyric line (the album between lines), with the album cover, the player's icon in the corner (Spotify, Apple Music, Chrome…), and a progress bar with elapsed time.
- **Word filter** (menu: **Discord word filter**, on by default): every text sent to Discord (title, artist, album, lyric, and so the status line) has filtered words replaced whole by asterisks, one per character ("**** You"). Levels: all swear words (default), slurs and offensive terms only, or off. The list and matching live in the shared core (`core/src/censor.rs`), so both apps filter identically; matching is per whole word after folding case, accents, simple leetspeak and stretched letters, so ordinary words ("Scunthorpe", "class", "cocktail") are left alone.
- Shows only while music plays; pausing clears it, like Discord's own Spotify status.

Turn it on from the tray menu (Windows) or the menu bar menu (macOS): **Show in Discord status**. Nothing to set up: Overtone uses its own Discord application (ID `1571417511696535643`, built into both apps). Discord's own **Activity Privacy → Share my activity** setting must be on.

## Art assets (maintainers)

The application's art assets hold the player icons. Upload every PNG in [`design/discord-assets/`](../design/discord-assets) under **Rich Presence → Art Assets** of the Overtone application on <https://discord.com/developers/applications>, keeping each file name (without `.png`) as the asset key: `overtone`, `spotify`, `applemusic`, `tidal`, `soundcloud`, `amazonmusic`, `youtubemusic`, `chrome`, `firefox`, `opera`, `brave`, `vlc`, `foobar2000`, `winamp`, `mpv`. Discord can take a few minutes to make new assets available. Without them the card still works: the player icon falls back to the player's own site icon by URL.

For development, a different application can be set: `"DiscordAppId"` in `%LOCALAPPDATA%\Overtone\settings.json` (Windows) or `defaults write com.bunkhq.overtone discordAppID <id>` (macOS).

## How it works

- Talks to the Discord desktop app over its local IPC pipe (`\\.\pipe\discord-ipc-N` on Windows, `$TMPDIR/discord-ipc-N` on macOS); no token. The only web request to Discord is reading the app's public asset list on connect. If Discord isn't running, Overtone retries every 20 s.
- **Cover:** Discord needs a public image URL, and the OS only gives Overtone pixels, so the cover is looked up (all free, keyless): the song on iTunes, then Deezer (exact title after stripping decoration such as "- Live", nearest duration); then the album by name on iTunes and Deezer; then a relaxed title match; then the artist's photo on Deezer. With nothing found, the big image shows the Overtone asset if uploaded, otherwise the player's icon.
- **Player icon:** chosen from the media session's app id. Overtone reads the app's uploaded asset names (a public Discord endpoint) when it connects and uses an uploaded icon when there is one; otherwise the player's site icon by URL. Windows Media Player has neither yet.
- **Rate limit:** Discord accepts about five updates per 20 s, so updates are coalesced to at most one every 4 s. Lyric lines shorter than that are skipped on the card (the overlay itself is unaffected). The progress bar's start is computed when an update is sent, from the position advanced to that moment, so Discord's elapsed time doesn't lag by the coalescing delay.
- Player glyphs are from [Simple Icons](https://simpleicons.org) (CC0); the marks remain their owners' trademarks and are used only to identify the player. Regenerate with `python scripts/gen-discord-assets.py`.
