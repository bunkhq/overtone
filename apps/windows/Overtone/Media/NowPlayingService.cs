using Microsoft.UI.Dispatching;
using Overtone.Core;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Overtone.Media;

/// <summary>Decoded artwork: straight RGBA for the palette engine, premultiplied BGRA for display.</summary>
public sealed record Artwork(byte[] Rgba, int Width, int Height, byte[] DisplayBgra, int DisplaySize);

public sealed record TrackInfo(string Title, string Artist, string Album, long? DurationMs, string SourceApp, Artwork? Art)
{
    public bool SameSong(TrackInfo? o) => o is not null && o.Title == Title && o.Artist == Artist && o.Album == Album && o.SourceApp == SourceApp;

    /// <summary>The line under the title: the artist, or the album when a file has no artist tag.</summary>
    public string Byline => string.IsNullOrWhiteSpace(Artist) ? Album : Artist;
}

/// <summary>What the overlay is presenting right now, for Discord Rich Presence. Position is the corrected clock's.</summary>
public sealed record PresenceState(TrackInfo Track, long PositionMs, bool Playing, long? DurationMs, string? Lyric);

public sealed record TimelineInfo(long PositionMs, long ReportedAtMs, bool Playing, double Rate, long? DurationMs, bool CanSeek);

/// <summary>
/// Reads the OS now-playing session (SMTC). Every player on docs/COMPATIBILITY.md
/// reports here, so this one class is the whole "any platform" integration.
/// Events are raised on the UI thread.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private const int DisplayArt = 256;
    private const int PaletteArt = 128;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _hooked = [];
    private CancellationTokenSource? _readCts;
    private TrackInfo? _track;

    // Resume tracking: some players (Spotify) don't rewrite the timeline when
    // playback resumes, so its timestamp is still the moment it paused.
    private bool? _lastPlaying;
    private long _resumedAt;

    /// <summary>
    /// While playing, the timeline is re-read every 2 s. Players fire timeline
    /// events unevenly (or drop them), and the clock adopts every correction, so
    /// polling a cached WinRT property keeps lyrics on the player's time.
    /// </summary>
    private readonly DispatcherQueueTimer _poll;

    // Music-only: sessions whose current item was judged not music, by app id, with
    // the title that was judged; a change of item clears the mark. Verdicts are
    // cached per app + artist + title so a song is never looked up twice.
    private readonly Dictionary<string, string> _notMusic = new();
    private readonly Dictionary<string, bool> _verdicts = new();
    private bool _musicOnly = true;

    public NowPlayingService(DispatcherQueue ui)
    {
        _ui = ui;
        _poll = ui.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => PushTimeline();
    }

    /// <summary>Raised with null when nothing is playing anywhere.</summary>
    public event Action<TrackInfo?>? TrackChanged;
    public event Action<TimelineInfo>? TimelineChanged;

    public TrackInfo? Track => _track;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => _ui.TryEnqueue(Rehook);
        _manager.CurrentSessionChanged += (_, _) => _ui.TryEnqueue(Reselect);
        Rehook();
    }

    public Task PlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>Ignore videos and other non-music media (tray menu).</summary>
    public bool MusicOnly
    {
        get => _musicOnly;
        set
        {
            if (_musicOnly == value) return;
            _musicOnly = value;
            _notMusic.Clear();
            _session = null; // re-pick and re-read under the new rule
            Reselect();
        }
    }

    /// <summary>Whether the current player accepts seeking (some sources expose no timeline control).</summary>
    public bool CanSeek { get; private set; }

    /// <summary>Seeks to <paramref name="positionMs"/> from the start of the track.</summary>
    public async Task SeekAsync(long positionMs)
    {
        if (_session is not { } s || !CanSeek) return;
        try
        {
            var start = s.GetTimelineProperties().StartTime;
            await s.TryChangePlaybackPositionAsync((start + TimeSpan.FromMilliseconds(Math.Max(0, positionMs))).Ticks);
        }
        catch { /* the player went away mid-command */ }
        // Players confirm with a timeline event, but not always promptly.
        await Task.Delay(400);
        _ui.TryEnqueue(PushTimeline);
    }

    private async Task Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session is { } s)
        {
            try { await command(s); } catch { /* the player went away mid-command */ }
        }
    }

    /// <summary>Hooks every session, so another player starting can take over.</summary>
    private void Rehook()
    {
        if (_manager is null) return;
        foreach (var s in _hooked)
        {
            s.MediaPropertiesChanged -= OnMedia;
            s.PlaybackInfoChanged -= OnPlayback;
            s.TimelinePropertiesChanged -= OnTimeline;
        }
        _hooked.Clear();
        foreach (var s in _manager.GetSessions())
        {
            s.MediaPropertiesChanged += OnMedia;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
            _hooked.Add(s);
        }
        Reselect();
    }

    private bool IsCurrent(GlobalSystemMediaTransportControlsSession s) => _session?.SourceAppUserModelId == s.SourceAppUserModelId;

    private void OnMedia(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs _) =>
        _ui.TryEnqueue(() =>
        {
            if (IsCurrent(s)) ScheduleRead();
            // A session skipped as "not music" gets a fresh look when its item changes.
            else if (_notMusic.Remove(s.SourceAppUserModelId)) Reselect();
        });

    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs _) =>
        _ui.TryEnqueue(() => { Reselect(); if (IsCurrent(s)) PushTimeline(); });

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs _) =>
        _ui.TryEnqueue(() => { if (IsCurrent(s)) PushTimeline(); });

    /// <summary>Prefer a session that is actually playing, then the OS's current one.</summary>
    private void Reselect()
    {
        if (_manager is null) return;
        var sessions = _manager.GetSessions();
        static bool Playing(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }
        // Sessions judged "not music" (a video in a browser tab) don't compete.
        sessions = sessions.Where(s => !_notMusic.ContainsKey(s.SourceAppUserModelId)).ToList();
        // Keep the current session while it plays, so two players don't fight.
        var current = sessions.FirstOrDefault(IsCurrent);
        var pick = current is not null && Playing(current)
            ? current
            : sessions.FirstOrDefault(Playing) ?? current ?? sessions.FirstOrDefault(x => x.SourceAppUserModelId == _manager.GetCurrentSession()?.SourceAppUserModelId) ?? sessions.FirstOrDefault();

        if (pick?.SourceAppUserModelId == _session?.SourceAppUserModelId && pick is not null)
        {
            _session = pick;
            return;
        }
        _session = pick;
        _lastPlaying = null; // a new session's first report is trusted as-is
        if (pick is null)
        {
            _poll.Stop();
            _track = null;
            TrackChanged?.Invoke(null);
            return;
        }
        ScheduleRead(immediate: true);
        PushTimeline();
    }

    private void ScheduleRead(bool immediate = false)
    {
        _readCts?.Cancel();
        var cts = _readCts = new CancellationTokenSource();
        _ = ReadAsync(immediate ? TimeSpan.Zero : Debounce, cts.Token);
    }

    private async Task ReadAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            var s = _session;
            if (s is null) return;
            var p = await s.TryGetMediaPropertiesAsync();
            if (ct.IsCancellationRequested || p is null) return;

            if (_musicOnly && !await IsMusicAsync(s.SourceAppUserModelId, p))
            {
                if (ct.IsCancellationRequested || _session != s) return;
                _notMusic[s.SourceAppUserModelId] = p.Title ?? "";
                _session = null;
                Reselect(); // another player, or nothing
                return;
            }
            if (ct.IsCancellationRequested) return;

            var tl = s.GetTimelineProperties();
            var duration = (tl.EndTime - tl.StartTime).TotalMilliseconds;
            var (title, artist) = Tidy(p.Title ?? "", p.Artist ?? "", p.AlbumArtist ?? "");
            var candidate = new TrackInfo(title, artist, p.AlbumTitle?.Trim() ?? "", duration > 0 ? (long)duration : null, s.SourceAppUserModelId, null);

            // Players fire title, artwork and timeline as separate events: skip re-reads
            // that change nothing, but take artwork that arrives after the title.
            if (candidate.SameSong(_track) && _track!.Art is not null) return;

            var art = await ReadArtworkAsync(p.Thumbnail);
            if (ct.IsCancellationRequested) return;
            if (candidate.SameSong(_track) && art is null) return;

            _track = candidate with { Art = art };
            TrackChanged?.Invoke(_track);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* session vanished between events */ }
    }

    private static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".alac", ".aif", ".aiff", ".ape", ".wv", ".mka"];

    /// <summary>
    /// Local files often arrive half-tagged: no artist (use the album artist), or no
    /// tags at all, when players report the file name as the title. A title that is a
    /// file name loses its extension and track number ("03 - Artist - Song.flac"),
    /// and without an artist tag an "Artist - Song" name is split.
    /// </summary>
    internal static (string Title, string Artist) Tidy(string title, string artist, string albumArtist)
    {
        title = title.Trim();
        artist = artist.Trim();
        if (artist.Length == 0) artist = albumArtist.Trim();
        var ext = Path.GetExtension(title);
        if (ext.Length == 0 || !AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return (title, artist);

        var name = title[..^ext.Length].Replace('_', ' ').Trim();
        // A track number: "03 - ", "3. ", "1-03 - " (disc-track), or "03 " with its leading
        // zero. A bare number and a space stays: "99 Problems" is a title.
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^(\d{1,2}(-\d{1,3})?\s*[.\-–)]\s*|0\d(-\d{1,3})?\s+)", "").Trim();
        if (artist.Length == 0 && name.Split(" - ", 2) is [var a, var t] && a.Trim().Length > 0 && t.Trim().Length > 0)
            return (t.Trim(), a.Trim());
        return (name.Length > 0 ? name : title, artist);
    }

    /// <summary>
    /// Is this item music? In order: a dedicated music player always is; an item
    /// with an album and artist is (YouTube Music, Spotify and Deezer on the web);
    /// one the OS marks as video isn't; anything else must be a song iTunes or
    /// Deezer knows (an official music video passes, a random video doesn't).
    /// </summary>
    private async Task<bool> IsMusicAsync(string app, GlobalSystemMediaTransportControlsSessionMediaProperties p)
    {
        var id = app.ToLowerInvariant();
        string[] musicApps = ["spotify", "applemusic", "appleinc.", "itunes", "cider", "tidal", "deezer", "amazonmusic",
            "qobuz", "foobar2000", "musicbee", "winamp", "aimp", "ytmdesktop", "th-ch", "youtube-music", "soundcloud"];
        if (musicApps.Any(id.Contains)) return true;
        if (!string.IsNullOrWhiteSpace(p.AlbumTitle) && !string.IsNullOrWhiteSpace(p.Artist)) return true;
        if (p.PlaybackType == MediaPlaybackType.Video) return false;
        // Local files, often untagged: Windows' Media Player and VLC play video too, so
        // they count only for non-video items; a title that is an audio file's name
        // counts anywhere; and so does an item the player marks as music with an album.
        string[] localPlayers = ["zunemusic", "wmplayer", "vlc"];
        if (localPlayers.Any(id.Contains)) return true;
        if (AudioExtensions.Contains(Path.GetExtension(p.Title ?? ""), StringComparer.OrdinalIgnoreCase)) return true;
        if (p.PlaybackType == MediaPlaybackType.Music && !string.IsNullOrWhiteSpace(p.AlbumTitle)) return true;
        if (string.IsNullOrWhiteSpace(p.Artist) || string.IsNullOrWhiteSpace(p.Title)) return false;

        var key = $"{id}\u0001{p.Artist}\u0001{p.Title}";
        if (_verdicts.TryGetValue(key, out var known)) return known;
        var (title, artist) = (p.Title, p.Artist);
        var found = await Task.Run(() => CoreLib.IsCatalogSong(title, artist));
        // Offline: give the benefit of the doubt rather than hide real music.
        var verdict = found ?? true;
        if (found is not null) _verdicts[key] = verdict;
        return verdict;
    }

    private void PushTimeline()
    {
        var s = _session;
        if (s is null) return;
        try
        {
            var tl = s.GetTimelineProperties();
            var info = s.GetPlaybackInfo();
            var duration = (tl.EndTime - tl.StartTime).TotalMilliseconds;
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (playing && _lastPlaying == false) _resumedAt = now;
            if (!playing) _resumedAt = 0;
            _lastPlaying = playing;

            var reportedAt = tl.LastUpdatedTime.ToUnixTimeMilliseconds();
            // Resumed, but the player hasn't rewritten its timeline since pausing: the
            // position it reports was true when playback resumed, not when it was written.
            if (playing && reportedAt < _resumedAt) reportedAt = _resumedAt;

            CanSeek = info.Controls.IsPlaybackPositionEnabled;
            if (playing) _poll.Start(); else _poll.Stop();

            TimelineChanged?.Invoke(new TimelineInfo(
                (long)(tl.Position - tl.StartTime).TotalMilliseconds,
                reportedAt,
                playing,
                info.PlaybackRate ?? 1.0,
                duration > 0 ? (long)duration : null,
                CanSeek));
        }
        catch { }
    }

    /// <summary>Decodes once per track: centre-cropped square (YouTube thumbnails are 16:9).</summary>
    private static async Task<Artwork?> ReadArtworkAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb is null) return null;
        try
        {
            using var stream = await thumb.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int w = (int)decoder.OrientedPixelWidth, h = (int)decoder.OrientedPixelHeight;
            if (w == 0 || h == 0) return null;

            async Task<(byte[] px, int size)> Square(int target, BitmapPixelFormat fmt, BitmapAlphaMode alpha)
            {
                var side = Math.Min(target, Math.Min(w, h));
                var scale = side / (double)Math.Min(w, h);
                uint sw = (uint)Math.Max(side, Math.Round(w * scale)), sh = (uint)Math.Max(side, Math.Round(h * scale));
                var transform = new BitmapTransform
                {
                    ScaledWidth = sw,
                    ScaledHeight = sh,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds { X = (sw - (uint)side) / 2, Y = (sh - (uint)side) / 2, Width = (uint)side, Height = (uint)side },
                };
                var data = await decoder.GetPixelDataAsync(fmt, alpha, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                return (data.DetachPixelData(), side);
            }

            var (rgba, ps) = await Square(PaletteArt, BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight);
            var (bgra, ds) = await Square(DisplayArt, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return new Artwork(rgba, ps, ps, bgra, ds);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        _readCts?.Cancel();
        foreach (var s in _hooked)
        {
            s.MediaPropertiesChanged -= OnMedia;
            s.PlaybackInfoChanged -= OnPlayback;
            s.TimelinePropertiesChanged -= OnTimeline;
        }
        _hooked.Clear();
    }
}
