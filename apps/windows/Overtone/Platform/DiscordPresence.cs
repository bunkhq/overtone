using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Overtone.Core;
using Overtone.Media;

namespace Overtone.Platform;

/// <summary>
/// Discord Rich Presence: "Listening to {artist}" in the member list, and on the
/// card the title, artist and current lyric, the album cover, the player's icon
/// and a progress bar.
/// Toggled from the tray. Discord allows about five activity updates per 20 s,
/// so updates are coalesced to one every 4 s at most, latest state winning.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(20);

    private readonly DispatcherQueue _ui;
    private readonly Settings _settings;
    private readonly DispatcherQueueTimer _sendTimer, _retryTimer;
    private readonly Dictionary<string, string?> _covers = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Art-asset keys uploaded to the Discord application; empty until fetched (or if none are).</summary>
    private HashSet<string> _assets = new(StringComparer.OrdinalIgnoreCase);

    private DiscordIpc? _ipc;
    private bool _connecting;
    private PresenceState? _state;
    private string? _lastSent;
    private long _lastStart;
    private DateTime _lastSendAt = DateTime.MinValue;

    public DiscordPresence(DispatcherQueue ui, Settings settings)
    {
        _ui = ui;
        _settings = settings;
        _sendTimer = ui.CreateTimer();
        _sendTimer.IsRepeating = false;
        _sendTimer.Tick += (_, _) => Flush();
        _retryTimer = ui.CreateTimer();
        _retryTimer.Interval = Retry;
        _retryTimer.Tick += (_, _) => _ = EnsureConnectedAsync();
        if (Enabled) _ = EnsureConnectedAsync();
    }

    /// <summary>The Discord application whose name and art assets the card uses (settings.json, DiscordAppId).</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(_settings.DiscordAppId);

    public bool Enabled
    {
        get => _settings.DiscordPresence;
        set
        {
            if (_settings.DiscordPresence == value) return;
            _settings.DiscordPresence = value;
            _settings.Save();
            if (value)
            {
                _lastSent = null;
                _ = EnsureConnectedAsync();
            }
            else
            {
                _sendTimer.Stop();
                _retryTimer.Stop();
                var ipc = _ipc;
                _ipc = null;
                _lastSent = null;
                if (ipc is not null) _ = ClearAndCloseAsync(ipc);
            }
        }
    }

    /// <summary>Called by the overlay on track, play-state, seek and lyric changes.</summary>
    public void Update(PresenceState? state)
    {
        _state = state;
        if (state is not null) _ = ResolveCoverAsync(state.Track);
        Schedule();
    }

    private void Schedule()
    {
        if (!Enabled || _ipc?.Connected != true) return;
        var wait = _lastSendAt + MinGap - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            Flush();
            return;
        }
        if (_sendTimer.IsRunning) return; // the pending send will pick up the latest state
        _sendTimer.Interval = wait;
        _sendTimer.Start();
    }

    private void Flush()
    {
        var ipc = _ipc;
        if (!Enabled || ipc?.Connected != true) return;
        var activity = Build(_state);
        var json = activity?.ToJsonString() ?? "null";
        if (json == _lastSent) return;
        _lastSent = json;
        _lastSendAt = DateTime.UtcNow;
        _ = SendAsync(ipc, activity);
    }

    private static async Task SendAsync(DiscordIpc ipc, JsonObject? activity)
    {
        try { await ipc.SetActivityAsync(activity); }
        catch (Exception ex) { Settings.LogError(ex); }
    }

    /// <summary>Paused or nothing playing: no activity, as Discord's own Spotify status does.</summary>
    private JsonObject? Build(PresenceState? s)
    {
        if (s is null || !s.Playing) return null;
        var t = s.Track;
        var title = Fit(string.IsNullOrWhiteSpace(t.Title) ? "Untitled" : t.Title);
        var artist = Fit(string.IsNullOrWhiteSpace(t.Artist) ? title : t.Artist);
        var service = Service(t.SourceApp);
        var serviceIcon = Icon(service);
        _covers.TryGetValue(CoverKey(t), out var cover);
        var album = string.IsNullOrWhiteSpace(t.Album) || t.Album == t.Title ? null : t.Album;

        // Card lines: title, artist, then the current lyric (the album between lines).
        // The member list reads "Listening to {artist}" from state.
        var activity = new JsonObject
        {
            ["type"] = 2,                 // Listening
            ["status_display_type"] = 1,  // member list shows "Listening to {state}"
            ["details"] = title,
            ["state"] = artist,
            ["assets"] = new JsonObject
            {
                ["large_image"] = cover ?? (_assets.Contains("overtone") ? "overtone" : serviceIcon),
                ["large_text"] = Fit(s.Lyric ?? album ?? title),
            },
        };
        if (serviceIcon is not null)
        {
            activity["assets"]!["small_image"] = serviceIcon;
            activity["assets"]!["small_text"] = service.Name;
        }
        if (s.DurationMs is > 0)
        {
            // Start/end make Discord draw the progress bar and elapsed time. Small
            // clock corrections keep the previous start so they don't cost an update.
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.PositionMs;
            if (Math.Abs(start - _lastStart) < 1500) start = _lastStart;
            _lastStart = start;
            activity["timestamps"] = new JsonObject { ["start"] = start, ["end"] = start + s.DurationMs.Value };
        }
        if (activity["assets"]!["large_image"] is null) activity["assets"]!.AsObject().Remove("large_image");
        return activity;
    }

    /// <summary>
    /// The player's icon: the uploaded art asset when the Discord app has it,
    /// otherwise the player's own site icon by public URL, so it shows with no setup.
    /// </summary>
    private string? Icon((string? Key, string Name, string? Domain) service)
    {
        if (service.Key is { } k && _assets.Contains(k)) return k;
        return service.Domain is { } d
            ? $"https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=https://{d}&size=128"
            : null;
    }

    /// <summary>Reads the app's uploaded asset names (a public endpoint), so missing icons fall back to URLs.</summary>
    private async Task LoadAssetsAsync(string appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://discord.com/api/v10/oauth2/applications/{appId}/assets"));
            var names = doc.RootElement.EnumerateArray().Select(a => a.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0);
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            _ui.TryEnqueue(() =>
            {
                _assets = set;
                _lastSent = null;
                Schedule();
            });
        }
        catch (Exception ex) { Settings.LogError(ex); }
    }

    /// <summary>Discord wants 2–128 characters per text field.</summary>
    private static string Fit(string s)
    {
        s = s.Trim();
        if (s.Length > 128) s = s[..127] + "…";
        return s.Length < 2 ? s + "​​"[..(2 - s.Length)] : s;
    }

    private static string CoverKey(TrackInfo t) => $"{t.Artist}\u0001{t.Title}";

    /// <summary>Discord needs a public image URL; the OS only gives pixels. Looked up once per song.</summary>
    private async Task ResolveCoverAsync(TrackInfo t)
    {
        var key = CoverKey(t);
        if (_covers.ContainsKey(key)) return;
        _covers[key] = null;
        string? url = null;
        try { url = await Task.Run(() => CoreLib.CoverUrl(t.Title, t.Artist, t.Album, t.DurationMs)); }
        catch (Exception ex) { Settings.LogError(ex); }
        if (url is null) return;
        _covers[key] = url;
        if (_state is not null && CoverKey(_state.Track) == key) Schedule();
    }

    private async Task EnsureConnectedAsync()
    {
        if (!Enabled || _connecting || _ipc?.Connected == true || !Configured) return;
        _connecting = true;
        var appId = _settings.DiscordAppId!.Trim();
        _ = LoadAssetsAsync(appId);
        var ipc = new DiscordIpc(appId);
        var ok = false;
        try { ok = await ipc.ConnectAsync(CancellationToken.None); }
        catch (Exception ex) { Settings.LogError(ex); }
        _connecting = false;

        if (!ok || !Enabled)
        {
            ipc.Dispose();
            if (Enabled) _retryTimer.Start(); // Discord not running yet: try again later
            return;
        }
        _retryTimer.Stop();
        ipc.Disconnected += reason => _ui.TryEnqueue(() =>
        {
            if (_ipc != ipc) return;
            _ipc = null;
            _lastSent = null;
            if (reason != "disposed" && Enabled) _retryTimer.Start();
        });
        _ipc = ipc;
        _lastSent = null;
        _lastSendAt = DateTime.MinValue;
        Schedule();
    }

    private static async Task ClearAndCloseAsync(DiscordIpc ipc)
    {
        try { await ipc.SetActivityAsync(null); } catch { }
        ipc.Dispose();
    }

    /// <summary>
    /// The player's art-asset key (uploaded to the Discord application, see
    /// design/discord-assets) and display name, from the SMTC source app id.
    /// </summary>
    public static (string? Key, string Name, string? Domain) Service(string sourceApp)
    {
        var id = sourceApp.ToLowerInvariant();
        (string, string, string)? hit = id switch
        {
            _ when id.Contains("spotify") => ("spotify", "Spotify", "open.spotify.com"),
            _ when id.Contains("cider") => ("applemusic", "Apple Music (Cider)", "music.apple.com"),
            _ when id.Contains("applemusic") || id.Contains("appleinc.") => ("applemusic", "Apple Music", "music.apple.com"),
            _ when id.Contains("itunes") => ("applemusic", "iTunes", "music.apple.com"),
            _ when id.Contains("tidal") => ("tidal", "TIDAL", "tidal.com"),
            _ when id.Contains("deezer") => ("deezer", "Deezer", "deezer.com"),
            _ when id.Contains("soundcloud") => ("soundcloud", "SoundCloud", "soundcloud.com"),
            _ when id.Contains("amazon") => ("amazonmusic", "Amazon Music", "music.amazon.com"),
            _ when id.Contains("youtube") || id.Contains("ytmdesktop") || id.Contains("th-ch") => ("youtubemusic", "YouTube Music", "music.youtube.com"),
            _ when id.Contains("qobuz") => ("qobuz", "Qobuz", "qobuz.com"),
            _ when id.Contains("chrome") => ("chrome", "Google Chrome", "www.google.com/chrome"),
            _ when id.Contains("msedge") || id.Contains("microsoftedge") => ("edge", "Microsoft Edge", "www.microsoft.com/edge"),
            _ when id.Contains("firefox") || id.Contains("308046b0af4a39cb") => ("firefox", "Firefox", "www.firefox.com"),
            _ when id.Contains("opera") => ("opera", "Opera", "www.opera.com"),
            _ when id.Contains("brave") => ("brave", "Brave", "brave.com"),
            _ when id.Contains("vlc") => ("vlc", "VLC", "www.videolan.org"),
            _ when id.Contains("foobar2000") => ("foobar2000", "foobar2000", "www.foobar2000.org"),
            _ when id.Contains("musicbee") => ("musicbee", "MusicBee", "getmusicbee.com"),
            _ when id.Contains("winamp") => ("winamp", "Winamp", "www.winamp.com"),
            _ when id.Contains("aimp") => ("aimp", "AIMP", "www.aimp.ru"),
            _ when id.Contains("mpv") => ("mpv", "mpv", "mpv.io"),
            _ when id.Contains("zunemusic") || id.Contains("wmplayer") => ("windowsmediaplayer", "Media Player", null!),
            _ => null,
        };
        if (hit is { } h) return (h.Item1, h.Item2, h.Item3);
        // Unknown player: no icon, but name it from its id ("Foo.exe" -> "Foo").
        var name = Path.GetFileNameWithoutExtension(sourceApp.Split('!')[^1]);
        return (null, string.IsNullOrWhiteSpace(name) ? "a media player" : name, null);
    }

    public void Dispose()
    {
        _sendTimer.Stop();
        _retryTimer.Stop();
        var ipc = _ipc;
        _ipc = null;
        if (ipc is not null)
        {
            // Best effort on quit: clear the card so it doesn't linger until Discord notices.
            try { ipc.SetActivityAsync(null).Wait(500); } catch { }
            ipc.Dispose();
        }
    }
}
