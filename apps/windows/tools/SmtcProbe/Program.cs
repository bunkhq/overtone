// SmtcProbe: prints every now-playing session Windows exposes, then streams changes.
// Proves the "any player" claim: whatever shows up here, Overtone can display.
// Usage: dotnet run [-- --once]
using Windows.Media.Control;

var once = args.Contains("--once");
static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

async Task Dump(string why)
{
    Console.WriteLine($"\n── {why} @ {DateTime.Now:HH:mm:ss} ─────────────");
    var current = manager.GetCurrentSession()?.SourceAppUserModelId;
    var sessions = manager.GetSessions();
    if (sessions.Count == 0) { Console.WriteLine("  (no sessions: nothing is reporting to SMTC)"); return; }
    foreach (var s in sessions)
    {
        var p = await s.TryGetMediaPropertiesAsync();
        var info = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        var mark = s.SourceAppUserModelId == current ? "*" : " ";
        Console.WriteLine($"{mark} {s.SourceAppUserModelId}");
        Console.WriteLine($"    {p.Title} — {(string.IsNullOrEmpty(p.Artist) ? "(no artist)" : p.Artist)}  [{p.AlbumTitle}]");
        Console.WriteLine($"    {info.PlaybackStatus}, rate {info.PlaybackRate?.ToString() ?? "-"}, " +
                          $"pos {Fmt(tl.Position)}/{Fmt(tl.EndTime)} (updated {tl.LastUpdatedTime.LocalDateTime:HH:mm:ss}), " +
                          $"art {(p.Thumbnail is null ? "none" : "yes")}");
    }
}

await Dump("sessions");
if (once) return;

void Hook(GlobalSystemMediaTransportControlsSession s)
{
    s.MediaPropertiesChanged += async (_, _) => await Dump($"media changed: {s.SourceAppUserModelId}");
    s.PlaybackInfoChanged += async (_, _) => await Dump($"playback changed: {s.SourceAppUserModelId}");
}
foreach (var s in manager.GetSessions()) Hook(s);
manager.SessionsChanged += async (m, _) => { foreach (var s in m.GetSessions()) Hook(s); await Dump("sessions changed"); };
manager.CurrentSessionChanged += async (_, _) => await Dump("current session changed");

Console.WriteLine("\nListening for changes. Ctrl+C to quit.");
await Task.Delay(Timeout.Infinite);
