using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Overtone.Media;
using Overtone.Overlay;

namespace Overtone.Platform;

/// <summary>
/// The tray icon and its native menu. Left click shows or hides the overlay;
/// right click opens the menu (a real Win32 menu, so keyboard and screen
/// readers get the system behaviour).
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly OverlayWindow _overlay;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly ToggleMenuFlyoutItem _cover, _gradient, _solid, _auto, _dark, _light;
    private readonly MenuFlyoutItem _showHide;
    private readonly ToggleMenuFlyoutItem _discord, _musicOnly, _nativeLook, _startup;
    private readonly DiscordPresence _presence;
    private readonly NowPlayingService _media;
    private readonly Settings _settings;
    private readonly TrayIconArt _art = new();
    private readonly System.Drawing.Icon _brandIcon = new(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"));
    private readonly Windows.UI.ViewManagement.UISettings _ui2 = new();
    private Overtone.Core.OtPalette? _palette;

    public Tray(OverlayWindow overlay, Settings settings, DiscordPresence presence, NowPlayingService media, Action quit)
    {
        _overlay = overlay;
        _presence = presence;
        _media = media;
        _settings = settings;
        _nativeLook = Choice("Native Windows look", () => _overlay.NativeLook = !_overlay.NativeLook);
        _musicOnly = Choice("Music only (ignore videos)", () =>
        {
            _settings.MusicOnly = !_settings.MusicOnly;
            _settings.Save();
            _media.MusicOnly = _settings.MusicOnly;
        });
        _discord = Choice("Show in Discord status", () => _presence.Enabled = !_presence.Enabled);
        _startup = Choice("Start with Windows", () => Startup.Enabled = !Startup.Enabled);

        _cover = Choice("Blurred cover", () => _overlay.Background = OverlayBackground.Cover);
        _gradient = Choice("Gradient", () => _overlay.Background = OverlayBackground.Gradient);
        _solid = Choice("Solid colour", () => _overlay.Background = OverlayBackground.Solid);
        _auto = Choice("Match the cover", () => _overlay.Tone = OverlayTone.Auto);
        _dark = Choice("Dark", () => _overlay.Tone = OverlayTone.Dark);
        _light = Choice("Light", () => _overlay.Tone = OverlayTone.Light);
        _showHide = new MenuFlyoutItem { Command = new Relay(() => _overlay.UserVisible = !_overlay.UserVisible) };

        var menu = new MenuFlyout();
        menu.Items.Add(_showHide);
        menu.Items.Add(new MenuFlyoutSeparator());
        var background = new MenuFlyoutSubItem { Text = "Background" };
        background.Items.Add(_cover);
        background.Items.Add(_gradient);
        background.Items.Add(_solid);
        var tone = new MenuFlyoutSubItem { Text = "Tone" };
        tone.Items.Add(_auto);
        tone.Items.Add(_dark);
        tone.Items.Add(_light);
        menu.Items.Add(_nativeLook);
        menu.Items.Add(background);
        menu.Items.Add(tone);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_musicOnly);
        menu.Items.Add(_discord);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_startup);
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit Overtone", Command = new Relay(quit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = "Overtone",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new Relay(() => _overlay.UserVisible = !_overlay.UserVisible),
            Icon = _brandIcon,
        };
        _icon.ForceCreate(false); // no Efficiency Mode: it would throttle lyric timing

        _overlay.StateChanged += Refresh;
        _overlay.PaletteChanged += SetPalette;
        // A light/dark taskbar switch re-picks the wave lightness.
        _ui2.ColorValuesChanged += (_, _) => _ui.TryEnqueue(() => SetPalette(_palette));
        Refresh();
    }

    /// <summary>Redraws the tray icon in the track's palette; null restores the brand icon.</summary>
    private void SetPalette(Overtone.Core.OtPalette? palette)
    {
        _palette = palette;
        try
        {
            _icon.Icon = palette is { } p ? _art.Render(p) : _brandIcon;
        }
        catch (Exception ex)
        {
            Settings.LogError(ex);
        }
    }

    public void SetTrack(TrackInfo? t)
    {
        // Shell tooltips cap at 127 characters.
        if (t is null) SetPalette(null);
        var tip = t is null ? "Overtone · nothing playing" : $"{t.Title} · {t.Artist}";
        _icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip;
    }

    /// <summary>
    /// A radio-style item. Clicking a toggle item flips its own check after the
    /// command runs, so the real state is written back on the next dispatcher turn;
    /// otherwise the click's flip could overwrite it and leave the check on the wrong row.
    /// </summary>
    private ToggleMenuFlyoutItem Choice(string text, Action pick) =>
        new() { Text = text, Command = new Relay(() => { pick(); _ui.TryEnqueue(DispatcherQueuePriority.Low, Refresh); }) };

    private void Refresh()
    {
        _showHide.Text = _overlay.UserVisible ? "Hide overlay" : "Show overlay";
        _cover.IsChecked = _overlay.Background == OverlayBackground.Cover;
        _gradient.IsChecked = _overlay.Background == OverlayBackground.Gradient;
        _solid.IsChecked = _overlay.Background == OverlayBackground.Solid;
        _auto.IsChecked = _overlay.Tone == OverlayTone.Auto;
        _dark.IsChecked = _overlay.Tone == OverlayTone.Dark;
        _light.IsChecked = _overlay.Tone == OverlayTone.Light;
        _discord.IsChecked = _presence.Enabled;
        _musicOnly.IsChecked = _settings.MusicOnly;
        _nativeLook.IsChecked = _overlay.NativeLook;
        _startup.IsChecked = Startup.Enabled;
        // The Background choice applies to the drawn card; native look uses tinted Acrylic.
        _cover.IsEnabled = _gradient.IsEnabled = _solid.IsEnabled = !_overlay.NativeLook;
        // Without an application ID there is nothing to connect as; say so in place.
        _discord.Text = _presence.Configured ? "Show in Discord status" : "Show in Discord status (needs app ID, see docs/DISCORD.md)";
    }

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
