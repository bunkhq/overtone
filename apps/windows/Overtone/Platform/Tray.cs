using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Overtone.Media;
using Overtone.Overlay;

namespace Overtone.Platform;

/// <summary>
/// The tray icon. Left click shows or hides the overlay; right click opens the
/// control panel (<see cref="TrayPanel"/>): now playing with transport, quick
/// toggles, the layout, and every setting one page in. The icon itself takes the
/// track's colours and its tooltip names the song.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly OverlayWindow _overlay;
    private readonly TrayPanel _panel;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly Settings _settings;
    private readonly Updater _updater;
    private readonly TrayIconArt _art = new();
    private readonly System.Drawing.Icon _brandIcon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"));
    private readonly Windows.UI.ViewManagement.UISettings _ui2 = new();
    private Overtone.Core.OtPalette? _palette;
    /// <summary>The overlay's palette in use (neutral without artwork); the panel takes its colours and Acrylic tint.</summary>
    private Overtone.Core.OtPalette _theme = Overtone.Core.CoreLib.Palette([], 0, 0);
    private TrackInfo? _track;
    private bool _playing;

    public Tray(OverlayWindow overlay, Settings settings, DiscordPresence presence, NowPlayingService media, Updater updater, Action quit)
    {
        _overlay = overlay;
        _settings = settings;
        _updater = updater;

        _panel = new TrayPanel(new PanelHost(overlay, settings, presence, media, updater, quit, About, () => SetPalette(_palette)));
        _icon = new TaskbarIcon
        {
            ToolTipText = "Overtone",
            NoLeftClickDelay = true,
            LeftClickCommand = new Relay(() => _overlay.UserVisible = !_overlay.UserVisible),
            RightClickCommand = new Relay(() => _panel.Toggle(IconRect())),
            Icon = _brandIcon,
        };
        _icon.ForceCreate(false); // no Efficiency Mode: it would throttle lyric timing

        _overlay.StateChanged += Refresh;
        _overlay.MenuRequested += _panel.OpenAtCursor;
        _overlay.Pressed += _panel.Dismiss;
        _overlay.PaletteChanged += SetPalette;
        _overlay.ThemeApplied += p =>
        {
            _theme = p;
            _panel.SetLook(_overlay.NativeLook, p);
        };
        _overlay.BackdropChanged += _panel.SetBackdrop;
        _updater.Changed += () =>
        {
            UpdateTooltip();
            Refresh();
        };
        _updater.Notice += (title, message) => _icon.ShowNotification(title, message);
        media.TrackChanged += SetTrack;
        media.TimelineChanged += t =>
        {
            if (t.Playing == _playing) return;
            _playing = t.Playing;
            _panel.SetTrack(_track, _playing);
        };
        // A light/dark taskbar switch re-picks the wave lightness.
        _ui2.ColorValuesChanged += (_, _) => _ui.TryEnqueue(() => SetPalette(_palette));
        Refresh();
    }

    /// <summary>Ctrl+Alt+Shift+M: the panel opens by the tray, as if its icon were clicked.</summary>
    public void TogglePanelFromKeyboard() => _panel.Toggle(IconRect(), atTray: true);

    /// <summary>The icon's rectangle on screen, so the menu opens beside it.</summary>
    private Win32.RECT? IconRect() => _icon.TrayIcon is { } t ? Win32.IconRect(t.Id, t.WindowHandle) : null;

    /// <summary>Redraws the tray icon in the track's palette; null (or the option off) restores the brand icon.</summary>
    private void SetPalette(Overtone.Core.OtPalette? palette)
    {
        _palette = palette;
        try
        {
            _icon.Icon = palette is { } p && _settings.ColorTrayIcon ? _art.Render(p) : _brandIcon;
        }
        catch (Exception ex)
        {
            Settings.LogError(ex);
        }
    }

    private void SetTrack(TrackInfo? t)
    {
        _track = t;
        if (t is null)
        {
            _playing = false;
            SetPalette(null);
        }
        UpdateTooltip();
        _panel.SetTrack(t, _playing);
    }

    private void UpdateTooltip()
    {
        var t = _track;
        var tip = t is null ? "Overtone · nothing playing" : string.IsNullOrWhiteSpace(t.Byline) ? t.Title : $"{t.Title} · {t.Byline}";
        if (_updater.Available is { } u) tip = $"Overtone {u.Version} is available\n{tip}";
        // Shell tooltips cap at 127 characters.
        _icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip;
    }

    /// <summary>The overlay's state or an update changed: the panel re-reads its controls.</summary>
    private void Refresh()
    {
        _panel.SetLook(_overlay.NativeLook, _theme);
        _panel.Refresh();
    }

    private static void About() =>
        // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND; no owner window.
        Win32.MessageBoxW(0,
            $"Overtone {Updater.CurrentVersion}\n\nNow playing and synced lyrics, floating over your desktop.\n\nhttps://github.com/{Updater.Repo}",
            "About Overtone", 0x00000040 | 0x00010000);

    public void Dispose()
    {
        _icon.Dispose();
        _art.Dispose();
        _brandIcon.Dispose();
    }

    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
