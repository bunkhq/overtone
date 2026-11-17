using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Overtone.Media;
using Overtone.Overlay;

namespace Overtone.Platform;

/// <summary>
/// The tray icon and its native menu, the same menu as the macOS menu bar's. Left
/// click shows or hides the overlay; right click opens the menu (a real Win32 menu,
/// so keyboard and screen readers get the system behaviour).
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly OverlayWindow _overlay;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly MenuFlyout _menu = new();
    private readonly DiscordPresence _presence;
    private readonly NowPlayingService _media;
    private readonly Settings _settings;
    private readonly Updater _updater;
    private readonly Action _quit;
    private readonly TrayIconArt _art = new();
    private readonly System.Drawing.Icon _brandIcon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"));
    private readonly Windows.UI.ViewManagement.UISettings _ui2 = new();
    private Overtone.Core.OtPalette? _palette;
    private TrackInfo? _track;
    private bool _playing;

    public Tray(OverlayWindow overlay, Settings settings, DiscordPresence presence, NowPlayingService media, Updater updater, Action quit)
    {
        _overlay = overlay;
        _presence = presence;
        _media = media;
        _settings = settings;
        _updater = updater;
        _quit = quit;

        _icon = new TaskbarIcon
        {
            ToolTipText = "Overtone",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = _menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new Relay(() => _overlay.UserVisible = !_overlay.UserVisible),
            Icon = _brandIcon,
        };
        _icon.ForceCreate(false); // no Efficiency Mode: it would throttle lyric timing

        _overlay.StateChanged += Refresh;
        _overlay.PaletteChanged += SetPalette;
        _updater.Changed += () =>
        {
            UpdateTooltip();
            Refresh();
        };
        _updater.Notice += (title, message) => _icon.ShowNotification(title, message);
        _media.TrackChanged += SetTrack;
        _media.TimelineChanged += t =>
        {
            if (t.Playing == _playing) return;
            _playing = t.Playing;
            Refresh();
        };
        // A light/dark taskbar switch re-picks the wave lightness.
        _ui2.ColorValuesChanged += (_, _) => _ui.TryEnqueue(() => SetPalette(_palette));
        Refresh();
    }

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
        Refresh();
    }

    private void UpdateTooltip()
    {
        var t = _track;
        var tip = t is null ? "Overtone · nothing playing" : $"{t.Title} · {t.Artist}";
        if (_updater.Available is { } u) tip = $"Overtone {u.Version} is available\n{tip}";
        // Shell tooltips cap at 127 characters.
        _icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip;
    }

    /// <summary>
    /// Rebuilds the menu. The tray shows it as a native popup menu built from these
    /// items each time it opens, which ignores Visibility, so rows come and go here.
    /// </summary>
    private void Refresh()
    {
        var items = _menu.Items;
        items.Clear();

        if (_updater.Available is { } update)
        {
            var label = _updater.Installing ? $"Installing Overtone {update.Version}…"
                : Updater.CanInstallInPlace ? $"Install update: Overtone {update.Version}"
                : $"Download update: Overtone {update.Version}…";
            items.Add(Item(label, () => _ = _updater.InstallAsync(_quit), enabled: !_updater.Installing));
            items.Add(new MenuFlyoutSeparator());
        }

        // Now playing, as on macOS: the title, then the artist and the player.
        if (_track is { } t)
        {
            items.Add(Item(Escape(t.Title), null, enabled: false));
            var sub = string.Join(" · ", new[] { t.Artist, DiscordPresence.Service(t.SourceApp).Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (sub.Length > 0) items.Add(Item(Escape(sub), null, enabled: false));
        }
        else
        {
            items.Add(Item("Nothing playing", null, enabled: false));
        }
        var has = _track is not null;
        items.Add(Item(_playing ? "Pause" : "Play", () => _ = _media.PlayPauseAsync(), enabled: has));
        items.Add(Item("Next track", () => _ = _media.NextAsync(), enabled: has));
        items.Add(Item("Previous track", () => _ = _media.PreviousAsync(), enabled: has));
        items.Add(new MenuFlyoutSeparator());

        items.Add(Toggle(Shortcut("Show overlay", HotKeys.ToggleOverlay), _overlay.UserVisible, () => _overlay.UserVisible = !_overlay.UserVisible));
        items.Add(Sub("Layout",
            Toggle("Pill", _overlay.Mode == OverlayMode.Pill, () => _overlay.SetMode(OverlayMode.Pill)),
            Toggle("Card", _overlay.Mode == OverlayMode.Card, () => _overlay.SetMode(OverlayMode.Card)),
            Toggle("Lyrics", _overlay.Mode == OverlayMode.Lyrics, () => _overlay.SetMode(OverlayMode.Lyrics)),
            new MenuFlyoutSeparator(),
            Item(Shortcut("Next layout", HotKeys.CycleLayout), _overlay.CycleMode)));
        items.Add(Toggle(Shortcut("Ghost mode", HotKeys.Ghost), _overlay.Ghost, () => _overlay.Ghost = !_overlay.Ghost));
        items.Add(Sub("Tone",
            Toggle("Match the cover", _overlay.Tone == OverlayTone.Auto, () => _overlay.Tone = OverlayTone.Auto),
            Toggle("Match system", _overlay.Tone == OverlayTone.System, () => _overlay.Tone = OverlayTone.System),
            Toggle("Dark", _overlay.Tone == OverlayTone.Dark, () => _overlay.Tone = OverlayTone.Dark),
            Toggle("Light", _overlay.Tone == OverlayTone.Light, () => _overlay.Tone = OverlayTone.Light)));
        // The Background choice applies to the drawn card; native look uses tinted Acrylic.
        var drawn = !_overlay.NativeLook;
        items.Add(Sub("Background",
            Toggle("Blurred cover", _overlay.Background == OverlayBackground.Cover, () => _overlay.Background = OverlayBackground.Cover, drawn),
            Toggle("Gradient", _overlay.Background == OverlayBackground.Gradient, () => _overlay.Background = OverlayBackground.Gradient, drawn),
            Toggle("Solid colour", _overlay.Background == OverlayBackground.Solid, () => _overlay.Background = OverlayBackground.Solid, drawn)));
        items.Add(Toggle("Native Windows look", _overlay.NativeLook, () => _overlay.NativeLook = !_overlay.NativeLook));
        items.Add(Toggle("Coloured tray icon", _settings.ColorTrayIcon, () =>
        {
            _settings.ColorTrayIcon = !_settings.ColorTrayIcon;
            _settings.Save();
            SetPalette(_palette);
        }));
        items.Add(Toggle("Music only (ignore videos)", _settings.MusicOnly, () =>
        {
            _settings.MusicOnly = !_settings.MusicOnly;
            _settings.Save();
            _media.MusicOnly = _settings.MusicOnly;
        }));
        items.Add(new MenuFlyoutSeparator());

        items.Add(Toggle("Show in Discord status", _presence.Enabled, () => _presence.Enabled = !_presence.Enabled));
        items.Add(Sub("Discord status shows",
            Toggle("Artist", _presence.StatusLine == DiscordStatusLine.Artist, () => _presence.StatusLine = DiscordStatusLine.Artist),
            Toggle("Song title", _presence.StatusLine == DiscordStatusLine.Title, () => _presence.StatusLine = DiscordStatusLine.Title),
            Toggle("Current lyric", _presence.StatusLine == DiscordStatusLine.Lyric, () => _presence.StatusLine = DiscordStatusLine.Lyric)));
        items.Add(Sub("Discord word filter",
            Toggle("All swear words", _presence.Filter == DiscordFilter.All, () => _presence.Filter = DiscordFilter.All),
            Toggle("Slurs and offensive terms", _presence.Filter == DiscordFilter.Offensive, () => _presence.Filter = DiscordFilter.Offensive),
            Toggle("Off", _presence.Filter == DiscordFilter.Off, () => _presence.Filter = DiscordFilter.Off)));
        items.Add(new MenuFlyoutSeparator());

        items.Add(Toggle("Start with Windows", Startup.Enabled, () => Startup.Enabled = !Startup.Enabled));
        if (_updater.Available is null)
            items.Add(Item("Check for updates…", () => _ = _updater.CheckAsync(manual: true)));
        items.Add(Item("About Overtone", About));
        items.Add(Item("Quit Overtone", _quit));
    }

    private static void About() =>
        // MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND; the overlay never takes focus, so no owner.
        Win32.MessageBoxW(0,
            $"Overtone {Updater.CurrentVersion}\n\nNow playing and synced lyrics, floating over your desktop.\n\nhttps://github.com/{Updater.Repo}",
            "About Overtone", 0x00000040 | 0x00010000);

    private static string Shortcut(string text, HotKeys.Shortcut shortcut) => $"{text}\t{shortcut.Label}";

    /// <summary>Native menus read "&amp;" as a mnemonic; double it in track text.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    private MenuFlyoutItem Item(string text, Action? run, bool enabled = true) =>
        new() { Text = text, IsEnabled = enabled && run is not null, Command = run is null ? null : Act(run) };

    private ToggleMenuFlyoutItem Toggle(string text, bool on, Action run, bool enabled = true) =>
        new() { Text = text, IsChecked = on, IsEnabled = enabled, Command = Act(run) };

    private static MenuFlyoutSubItem Sub(string text, params MenuFlyoutItemBase[] children)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        foreach (var c in children) sub.Items.Add(c);
        return sub;
    }

    /// <summary>Runs <paramref name="run"/>, then rebuilds the menu so its checks show the new state.</summary>
    private Relay Act(Action run) => new(() =>
    {
        run();
        _ui.TryEnqueue(DispatcherQueuePriority.Low, Refresh);
    });

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
