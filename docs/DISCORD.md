# Discord Rich Presence

Overtone can show what you're listening to on your Discord profile:

- **Member list status:** "Listening to {artist}".
- **Profile card:** the song title, the artist, and the current lyric line (the album between lines), with the album cover, the player's icon in the corner (Spotify, Apple Music, Chrome…), and a progress bar with elapsed time.
- Shows only while music plays; pausing clears it, like Discord's own Spotify status.

Toggle it from the tray menu: **Show in Discord status**.

## One-time setup

Rich Presence belongs to a Discord application: its name is the card's heading, and its art assets hold the player icons. Discord doesn't let one app ID be shared safely, so create your own (two minutes):

1. Open <https://discord.com/developers/applications> and click **New Application**. Name it **Overtone** (this name appears on the card).
2. On **General Information**, copy the **Application ID**.
3. Optional: open **Rich Presence → Art Assets** and upload every PNG in [`design/discord-assets/`](../design/discord-assets), keeping each file name (without `.png`) as the asset key: `overtone`, `spotify`, `applemusic`, `tidal`, `soundcloud`, `amazonmusic`, `youtubemusic`, `chrome`, `firefox`, `opera`, `brave`, `vlc`, `foobar2000`, `winamp`, `mpv`. Discord can take a few minutes to make new assets available. Without them the card still works: the player icon falls back to the player's own site icon by URL, which looks slightly less uniform.
4. Quit Overtone from the tray, add the ID to `%LOCALAPPDATA%\Overtone\settings.json`, and start it again:

   ```json
   "DiscordAppId": "123456789012345678"
   ```

5. Tray menu → **Show in Discord status**. Discord's own **Activity Privacy → Share my activity** setting must be on.

## How it works

- Talks to the Discord desktop app over its local IPC pipe (`\\.\pipe\discord-ipc-N`); no token. The only web request to Discord is reading the app's public asset list on connect. If Discord isn't running, Overtone retries every 20 s.
- **Cover:** Discord needs a public image URL, and the OS only gives Overtone pixels, so the cover is looked up (all free, keyless): the song on iTunes, then Deezer (exact title after stripping decoration such as "- Live", nearest duration); then the album by name on iTunes and Deezer; then a relaxed title match; then the artist's photo on Deezer. With nothing found, the big image shows the Overtone asset if uploaded, otherwise the player's icon.
- **Player icon:** chosen from the media session's app id. Overtone reads the app's uploaded asset names (a public Discord endpoint) when it connects and uses an uploaded icon when there is one; otherwise the player's site icon by URL. Windows Media Player has neither yet.
- **Rate limit:** Discord accepts about five updates per 20 s, so updates are coalesced to at most one every 4 s. Lyric lines shorter than that are skipped on the card (the overlay itself is unaffected).
- Player glyphs are from [Simple Icons](https://simpleicons.org) (CC0); the marks remain their owners' trademarks and are used only to identify the player. Regenerate with `python scripts/gen-discord-assets.py`.
