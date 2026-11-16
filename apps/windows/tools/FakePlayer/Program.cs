// A silent, scriptable media session for testing Overtone without touching a real player.
//
//   dotnet run -- "Title" "Artist" "Album" <seconds> [artwork.png] [startAtSeconds]
//
// Plays generated silence at volume 0 through MediaPlayer, so it shows up in SMTC
// exactly like a real player (title, artist, artwork, timeline, play/pause/seek).
// Ctrl+C to quit.
using System.Text;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

var title = args.ElementAtOrDefault(0) ?? "Test Track";
var artist = args.ElementAtOrDefault(1) ?? "Overtone";
var album = args.ElementAtOrDefault(2) ?? "";
var seconds = int.TryParse(args.ElementAtOrDefault(3), out var s) ? s : 180;
var art = args.ElementAtOrDefault(4);
var startAt = int.TryParse(args.ElementAtOrDefault(5), out var a) ? a : 0;

var player = new MediaPlayer { Volume = 0, IsLoopingEnabled = false };
player.CommandManager.IsEnabled = true;
// With the command manager on, MediaPlayer drives SMTC from the item's display
// properties, so metadata goes on the item rather than the DisplayUpdater.
var item = new MediaPlaybackItem(MediaSource.CreateFromStream(Silence(seconds).AsRandomAccessStream(), "audio/wav"));
var props = item.GetDisplayProperties();
props.Type = MediaPlaybackType.Music;
props.MusicProperties.Title = title;
props.MusicProperties.Artist = artist;
props.MusicProperties.AlbumTitle = album;
if (art is not null && File.Exists(art))
    props.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(art)));
item.ApplyDisplayProperties(props);
player.Source = item;
player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

player.CommandManager.NextReceived += (_, _) => Console.WriteLine("next (ignored)");
player.CommandManager.PreviousReceived += (_, _) => { player.PlaybackSession.Position = TimeSpan.Zero; Console.WriteLine("previous: restart"); };
player.PlaybackSession.PlaybackStateChanged += (ps, _) => Console.WriteLine($"state: {ps.PlaybackState} @ {ps.Position:m\\:ss}");

player.MediaOpened += (_, _) =>
{
    player.PlaybackSession.Position = TimeSpan.FromSeconds(startAt);
    player.Play();
};
Console.WriteLine($"Playing \"{title}\" by {artist} ({seconds}s) silently from {startAt}s. Ctrl+C to quit.");
var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
await quit.Task;
player.Dispose();

static MemoryStream Silence(int seconds)
{
    const int rate = 8000;
    var dataLen = rate * 2 * seconds;
    var ms = new MemoryStream(44 + dataLen);
    var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
    w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(dataLen);
    w.Write(new byte[dataLen]);
    w.Flush();
    ms.Position = 0;
    return ms;
}
