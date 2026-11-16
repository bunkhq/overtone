using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Overtone.Media;
using Overtone.Overlay;
using Overtone.Platform;

namespace Overtone;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private NowPlayingService? _media;
    private OverlayWindow? _overlay;
    private Tray? _tray;
    private DiscordPresence? _presence;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Settings.LogError(e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One overlay per user session: a second launch just exits.
        _singleInstance = new Mutex(true, @"Local\Overtone.Overlay", out var first);
        if (!first)
        {
            Exit();
            return;
        }

        var settings = Settings.Load();
        _media = new NowPlayingService(DispatcherQueue.GetForCurrentThread()) { MusicOnly = settings.MusicOnly };
        _overlay = new OverlayWindow(settings, _media, new LyricsService());
        _presence = new DiscordPresence(DispatcherQueue.GetForCurrentThread(), settings);
        _overlay.PresenceChanged += _presence.Update;
        _tray = new Tray(_overlay, settings, _presence, _media, Quit);
        _media.TrackChanged += _tray.SetTrack;

        await _media.StartAsync();
    }

    private void Quit()
    {
        _tray?.Dispose();
        _presence?.Dispose();
        _media?.Dispose();
        _overlay?.Shutdown();
        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
