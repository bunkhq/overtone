using Overtone.Core;
using Overtone.Platform;
using System.Text.Json;

namespace Overtone.Media;

public sealed record LyricLine(long StartMs, string Text)
{
    public bool IsGap => Text.Length == 0;
}

public enum LyricsStatus { Loading, Found, Instrumental, NotFound, Error }

public sealed record LyricsDoc(LyricsStatus Status, bool Synced, IReadOnlyList<LyricLine> Lines)
{
    public static readonly LyricsDoc Loading = new(LyricsStatus.Loading, false, []);
    public static readonly LyricsDoc NotFound = new(LyricsStatus.NotFound, false, []);
    public static readonly LyricsDoc Instrumental = new(LyricsStatus.Instrumental, false, []);

    public bool HasLines => Status == LyricsStatus.Found && Lines.Count > 0;

    /// <summary>
    /// Active line at <paramref name="positionMs"/>; -1 before the first line.
    /// Unsynced lyrics advance proportionally through the track when its length is known.
    /// </summary>
    public int LineAt(long positionMs, long? durationMs)
    {
        if (!HasLines) return -1;
        if (!Synced)
            return durationMs is > 0 ? (int)Math.Clamp(positionMs * Lines.Count / durationMs.Value, 0, Lines.Count - 1) : 0;
        int lo = 0, hi = Lines.Count; // first index with StartMs > position
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].StartMs <= positionMs) lo = mid + 1; else hi = mid;
        }
        return lo - 1;
    }

    /// <summary>Playback position at which the active line next changes.</summary>
    public long? NextChangeMs(long positionMs, long? durationMs)
    {
        if (!HasLines) return null;
        if (!Synced)
        {
            if (durationMs is not > 0) return null;
            var next = LineAt(positionMs, durationMs) + 1;
            return next < Lines.Count ? (next * durationMs.Value + Lines.Count - 1) / Lines.Count : null;
        }
        var i = LineAt(positionMs, durationMs) + 1;
        return i < Lines.Count ? Lines[i].StartMs : null;
    }
}

/// <summary>
/// Looks lyrics up through the Rust core (LRCLIB), off the UI thread. An in-memory
/// cache in front of the core's on-disk one (%LOCALAPPDATA%\Overtone\Cache\lyrics, as
/// on macOS), so songs played before load without the network.
/// </summary>
public sealed class LyricsService
{
    private static readonly string CacheDir = Path.Combine(Settings.DataDir, "Cache", "lyrics");

    private readonly Dictionary<string, LyricsDoc> _cache = new();
    private readonly object _gate = new();

    public async Task<LyricsDoc> GetAsync(TrackInfo track)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            return LyricsDoc.NotFound;

        // Durations differ by a second or two across reports; bucket them.
        var key = $"{track.Artist}\u0001{track.Title}\u0001{(track.DurationMs ?? 0) / 4000}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        var doc = await Task.Run(() =>
        {
            using var json = CoreLib.LookupLyrics(CacheDir, track.Title, track.Artist, track.Album, track.DurationMs);
            return Parse(json.RootElement);
        });

        // Errors are transient (offline, timeout): don't cache them.
        if (doc.Status != LyricsStatus.Error)
            lock (_gate) _cache[key] = doc;
        return doc;
    }

    private static LyricsDoc Parse(JsonElement root)
    {
        switch (root.GetProperty("status").GetString())
        {
            case "found":
                var lines = root.GetProperty("lines").EnumerateArray()
                    .Select(l => new LyricLine(l.GetProperty("t").GetInt64(), l.GetProperty("text").GetString() ?? ""))
                    .ToList();
                return new LyricsDoc(LyricsStatus.Found, root.GetProperty("synced").GetBoolean(), lines);
            case "instrumental":
                return LyricsDoc.Instrumental;
            case "not_found":
                return LyricsDoc.NotFound;
            default:
                return new LyricsDoc(LyricsStatus.Error, false, []);
        }
    }
}
