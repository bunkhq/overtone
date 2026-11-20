using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Overtone.Media;
using Overtone.Overlay;
using Windows.Foundation;
using Windows.System;

namespace Overtone.Platform;

/// <summary>Everything the menu controls; the tray owns these.</summary>
public sealed record PanelHost(
    OverlayWindow Overlay,
    Settings Settings,
    DiscordPresence Presence,
    NowPlayingService Media,
    Updater Updater,
    Action Quit,
    Action About,
    Action TrayIconChanged);

/// <summary>
/// The tray menu: a now-playing strip with transport, then one row per setting.
/// On/off rows switch in place, the layout's pills pick in place, and the longer
/// lists (background, tone, Discord's status and filter, more settings) open a
/// flyout to the side, on hover or click, as a context menu's submenus do. Settings
/// leave the menu open; Quit, About and the update actions close it.
/// </summary>
public sealed partial class TrayPanelView : UserControl
{
    private const double FlyoutGap = 4; // DIPs between the menu and its flyout

    private PanelHost? _host;
    private TrackInfo? _track;
    private bool _playing;
    private bool _syncing;

    // The open flyout's row, and the hover timer that opens or closes flyouts.
    private ToggleButton? _openRow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hover;
    private Action? _hoverAction;
    private readonly Dictionary<ToggleButton, StackPanel> _lists = new();

    public TrayPanelView()
    {
        InitializeComponent();
        Theme = new PaletteTheme(Resources);
        VersionText.Text = Updater.CurrentVersion;
        OverlayKey.Text = HotKeys.ToggleOverlay.Label;
        GhostKey.Text = HotKeys.Ghost.Label;
        AutomationProperties.SetAcceleratorKey(OverlayToggle, HotKeys.ToggleOverlay.Label);
        AutomationProperties.SetAcceleratorKey(GhostToggle, HotKeys.Ghost.Label);
        Motion.EnableTranslation(FlyoutCard);

        _hover = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hover.IsRepeating = false;
        _hover.Tick += (_, _) =>
        {
            var act = _hoverAction;
            _hoverAction = null;
            act?.Invoke();
        };

        KeyDown += OnMenuKey;
        FlyoutCard.KeyDown += OnFlyoutKey;
        FlyoutCard.PointerEntered += (_, _) => CancelHover();
        // A press anywhere in the menu but the open flyout's own row closes the flyout.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (_openRow is { } row && !IsWithin(e.OriginalSource as DependencyObject, row)
                && !IsWithin(e.OriginalSource as DependencyObject, FlyoutCard))
                CloseFlyout();
        }), handledEventsToo: true);
    }

    /// <summary>The menu's colours: the overlay's palette, on the menu's own brushes.</summary>
    public PaletteTheme Theme { get; }

    /// <summary>Esc with no flyout open, or an action that ends with the menu closed.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the menu's height may have changed (an update appearing).</summary>
    public event Action? HeightChanged;

    /// <summary>The screen's work area in this window's DIPs, so a flyout opens where it fits.</summary>
    public Rect WorkArea { get; set; } = new(-10000, -10000, 20000, 20000);

    public bool FlyoutOpen => Flyout.IsOpen;

    public void Bind(PanelHost host)
    {
        _host = host;
        var o = host.Overlay;

        PrevButton.Click += (_, _) => _ = host.Media.PreviousAsync();
        NextButton.Click += (_, _) => _ = host.Media.NextAsync();
        PlayButton.Click += (_, _) => _ = host.Media.PlayPauseAsync();

        Toggle(OverlayToggle, on => o.UserVisible = on);
        Toggle(LyricsOnlyToggle, on => o.LyricsOnly = on);
        Toggle(GhostToggle, on => o.Ghost = on);
        Toggle(NativeToggle, on => o.NativeLook = on);
        Toggle(DiscordToggle, on => host.Presence.Enabled = on);

        Radio(PillPill, () => o.SetMode(OverlayMode.Pill));
        Radio(CardPill, () => o.SetMode(OverlayMode.Card));
        Radio(LyricsPill, () => o.SetMode(OverlayMode.Lyrics));

        Radio(BgCover, () => o.Background = OverlayBackground.Cover);
        Radio(BgGradient, () => o.Background = OverlayBackground.Gradient);
        Radio(BgSolid, () => o.Background = OverlayBackground.Solid);
        Radio(BgNone, () => o.Background = OverlayBackground.None);
        Radio(ToneAuto, () => o.Tone = OverlayTone.Auto);
        Radio(ToneSystem, () => o.Tone = OverlayTone.System);
        Radio(ToneDark, () => o.Tone = OverlayTone.Dark);
        Radio(ToneLight, () => o.Tone = OverlayTone.Light);
        Radio(StatusArtist, () => host.Presence.StatusLine = DiscordStatusLine.Artist);
        Radio(StatusTitle, () => host.Presence.StatusLine = DiscordStatusLine.Title);
        Radio(StatusAlbum, () => host.Presence.StatusLine = DiscordStatusLine.Album);
        Radio(StatusLyric, () => host.Presence.StatusLine = DiscordStatusLine.Lyric);
        Radio(FilterAll, () => host.Presence.Filter = DiscordFilter.All);
        Radio(FilterOffensive, () => host.Presence.Filter = DiscordFilter.Offensive);
        Radio(FilterOff, () => host.Presence.Filter = DiscordFilter.Off);

        Toggle(SnapToggle, on => o.SnapToEdges = on);
        Toggle(GhostDimToggle, on => o.GhostDims = on);
        Toggle(MatchBehindToggle, on => o.MatchBehind = on);
        Toggle(TextColoursToggle, on => o.TextOnlyColours = on);
        Toggle(TextShadowToggle, on => o.TextOnlyShadow = on);
        Toggle(MusicOnlyToggle, on =>
        {
            host.Settings.MusicOnly = on;
            host.Settings.Save();
            host.Media.MusicOnly = on;
        });
        Toggle(StartupToggle, on => Startup.Enabled = on);
        Toggle(TrayColourToggle, on =>
        {
            host.Settings.ColorTrayIcon = on;
            host.Settings.Save();
            host.TrayIconChanged();
        });

        Sub(BackgroundRow, BackgroundList);
        Sub(ToneRow, ToneList);
        Sub(StatusRow, StatusList);
        Sub(FilterRow, FilterList);
        Sub(MoreRow, MoreList);
        // Resting on any other row closes an open flyout, as in a context menu.
        foreach (var row in new FrameworkElement[] { OverlayToggle, LayoutRow, LyricsOnlyToggle, GhostToggle, NativeToggle, DiscordToggle, UpdateRow, QuitRow })
            row.PointerEntered += (_, _) => { if (_openRow is not null) Hover(320, CloseFlyout); else CancelHover(); };

        UpdateRow.Click += (_, _) =>
        {
            CloseRequested?.Invoke();
            _ = host.Updater.InstallAsync(host.Quit);
        };
        CheckRow.Click += (_, _) =>
        {
            CloseRequested?.Invoke();
            _ = host.Updater.CheckAsync(manual: true);
        };
        AboutRow.Click += (_, _) =>
        {
            CloseRequested?.Invoke();
            host.About();
        };
        QuitRow.Click += (_, _) => host.Quit();
    }

    /// <summary>A switch acts on the user's click only, never on the menu syncing its state.</summary>
    private void Toggle(ToggleButton t, Action<bool> set) =>
        t.Click += (_, _) =>
        {
            if (_syncing) return;
            set(t.IsChecked == true);
            Refresh();
        };

    /// <summary>
    /// One choice of a single-choice group: a click picks it, and the refresh that
    /// follows checks it alone (clicking the chosen one leaves it chosen).
    /// </summary>
    private void Radio(ToggleButton r, Action set) =>
        r.Click += (_, _) =>
        {
            if (_syncing) return;
            set();
            Refresh();
        };

    public void SetTrack(TrackInfo? track, bool playing)
    {
        _track = track;
        _playing = playing;
        Refresh();
    }

    /// <summary>The blurred cover behind the strip (drawn look), BGRA; null clears it.</summary>
    public void SetBackdrop(byte[]? bgra, int size) => BackdropFill.ImageSource = bgra is null ? null : Bitmap(bgra, size);

    // The menu's own bitmaps: an image source drawn in the overlay's window can't also be drawn in this one.
    private Artwork? _art;

    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap Bitmap(byte[] bgra, int size)
    {
        var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(size, size);
        using (var s = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(wb.PixelBuffer))
            s.Write(bgra, 0, Math.Min(bgra.Length, size * size * 4));
        wb.Invalidate();
        return wb;
    }

    /// <summary>
    /// Native look: transparent over the window's tinted Acrylic, so the ground and the
    /// strip's cover give way to the material. Drawn look: the palette's ground, and
    /// the blurred cover under a scrim behind the song. The flyout keeps the palette's
    /// ground either way: its window has no frame to round a material's corners.
    /// </summary>
    public void SetNative(bool native)
    {
        Ground.Visibility = Backdrop.Visibility = HeaderScrim.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Re-reads every control's state from the app.</summary>
    public void Refresh()
    {
        if (_host is not { } h) return;
        var o = h.Overlay;
        _syncing = true;
        try
        {
            // Now playing.
            var t = _track;
            TitleText.Text = t is null ? "Nothing playing" : string.IsNullOrWhiteSpace(t.Title) ? "Untitled" : t.Title;
            BylineText.Text = t is null ? "Play something in any app"
                : string.Join(" · ", new[] { t.Byline, DiscordPresence.Service(t.SourceApp).Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!ReferenceEquals(t?.Art, _art))
            {
                _art = t?.Art;
                ArtFill.ImageSource = _art is { } a ? Bitmap(a.DisplayBgra, a.DisplaySize) : null;
            }
            ArtImage.Visibility = t?.Art is null ? Visibility.Collapsed : Visibility.Visible;
            foreach (var b in new Control[] { PrevButton, PlayButton, NextButton }) b.IsEnabled = t is not null;
            PlayGlyph.Glyph = _playing ? "" : ""; // PauseSolid / PlaySolid
            AutomationProperties.SetName(PlayButton, _playing ? "Pause" : "Play");

            // Overlay.
            OverlayToggle.IsChecked = o.UserVisible;
            LyricsOnlyToggle.IsChecked = o.LyricsOnly;
            GhostToggle.IsChecked = o.Ghost;
            // Lyrics only has one line or several, and no player card.
            var lyricsOnly = o.LyricsOnly;
            PillPillText.Text = lyricsOnly ? "One line" : "Pill";
            LyricsPillText.Text = lyricsOnly ? "Several" : "Lyrics";
            AutomationProperties.SetName(PillPill, lyricsOnly ? "One line" : "Pill");
            AutomationProperties.SetName(LyricsPill, lyricsOnly ? "Several lines" : "Lyrics");
            CardPill.Visibility = lyricsOnly ? Visibility.Collapsed : Visibility.Visible;
            PillPill.IsChecked = o.Mode == OverlayMode.Pill;
            CardPill.IsChecked = o.Mode == OverlayMode.Card;
            LyricsPill.IsChecked = o.Mode == OverlayMode.Lyrics;

            // Look. The native look draws its own background (tinted Acrylic), so the
            // card backgrounds only apply without it; None works either way.
            var bg = o.Background;
            BgCover.IsChecked = bg == OverlayBackground.Cover;
            BgGradient.IsChecked = bg == OverlayBackground.Gradient;
            BgSolid.IsChecked = bg == OverlayBackground.Solid;
            BgNone.IsChecked = bg == OverlayBackground.None;
            var drawn = !o.NativeLook || bg == OverlayBackground.None;
            BgCover.IsEnabled = BgGradient.IsEnabled = BgSolid.IsEnabled = drawn;
            MatchBehindPanel.Visibility = bg == OverlayBackground.None ? Visibility.Visible : Visibility.Collapsed;
            MatchBehindToggle.IsChecked = o.MatchBehind;
            TextColoursToggle.IsChecked = o.TextOnlyColours;
            TextShadowToggle.IsChecked = o.TextOnlyShadow;
            BackgroundNote.Text = bg == OverlayBackground.None
                ? (o.TextOnlyColours ? "Text in the album's colours" : "Black or white text")
                    + (o.MatchBehind ? ", dark over light screens and light over dark ones." : ", in the tone you pick.")
                : drawn ? "" : "The native look uses tinted Acrylic. Turn it off to pick a background.";
            BackgroundNote.Visibility = BackgroundNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BackgroundValue.Text = bg == OverlayBackground.None ? "None"
                : o.NativeLook ? "Acrylic"
                : bg switch { OverlayBackground.Gradient => "Gradient", OverlayBackground.Solid => "Solid colour", _ => "Blurred cover" };
            ToneAuto.IsChecked = o.Tone == OverlayTone.Auto;
            ToneSystem.IsChecked = o.Tone == OverlayTone.System;
            ToneDark.IsChecked = o.Tone == OverlayTone.Dark;
            ToneLight.IsChecked = o.Tone == OverlayTone.Light;
            ToneValue.Text = o.Tone switch
            {
                OverlayTone.System => "Match system",
                OverlayTone.Dark => "Dark",
                OverlayTone.Light => "Light",
                _ => "Match the cover",
            };
            NativeToggle.IsChecked = o.NativeLook;
            NativeToggle.IsEnabled = bg != OverlayBackground.None;

            // Discord: what the status shows means nothing while it's off.
            var p = h.Presence;
            DiscordToggle.IsChecked = p.Enabled;
            StatusArtist.IsChecked = p.StatusLine == DiscordStatusLine.Artist;
            StatusTitle.IsChecked = p.StatusLine == DiscordStatusLine.Title;
            StatusAlbum.IsChecked = p.StatusLine == DiscordStatusLine.Album;
            StatusLyric.IsChecked = p.StatusLine == DiscordStatusLine.Lyric;
            StatusValue.Text = p.StatusLine switch
            {
                DiscordStatusLine.Title => "Song title",
                DiscordStatusLine.Album => "Album",
                DiscordStatusLine.Lyric => "Current lyric",
                _ => "Artist",
            };
            FilterAll.IsChecked = p.Filter == DiscordFilter.All;
            FilterOffensive.IsChecked = p.Filter == DiscordFilter.Offensive;
            FilterOff.IsChecked = p.Filter == DiscordFilter.Off;
            FilterValue.Text = p.Filter switch
            {
                DiscordFilter.All => "All swears",
                DiscordFilter.Offensive => "Slurs only",
                _ => "Off",
            };
            StatusRow.IsEnabled = FilterRow.IsEnabled = p.Enabled;
            if (!p.Enabled && (_openRow == StatusRow || _openRow == FilterRow)) CloseFlyout();

            // More.
            SnapToggle.IsChecked = o.SnapToEdges;
            GhostDimToggle.IsChecked = o.GhostDims;
            MusicOnlyToggle.IsChecked = h.Settings.MusicOnly;
            StartupToggle.IsChecked = Startup.Enabled;
            TrayColourToggle.IsChecked = h.Settings.ColorTrayIcon;

            // Updates.
            var hadUpdate = UpdateRow.Visibility == Visibility.Visible;
            if (h.Updater.Available is { } u)
            {
                UpdateText.Text = h.Updater.Installing ? $"Installing {u.Version}…" : $"Update to {u.Version}";
                UpdateRow.IsEnabled = !h.Updater.Installing;
                AutomationProperties.SetName(UpdateRow, Updater.CanInstallInPlace ? $"Install Overtone {u.Version}" : $"Download Overtone {u.Version}");
                UpdateRow.Visibility = Visibility.Visible;
            }
            else UpdateRow.Visibility = Visibility.Collapsed;
            if (hadUpdate != (UpdateRow.Visibility == Visibility.Visible)) HeightChanged?.Invoke();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The menu's height at <paramref name="width"/>, in DIPs.</summary>
    public double MeasureHeight(double width)
    {
        Root.Measure(new Size(width, double.PositiveInfinity));
        return Math.Ceiling(Root.DesiredSize.Height);
    }

    /// <summary>Called each time the menu opens.</summary>
    public void Reset() => CloseFlyout();

    /// <summary>The control keyboard focus starts on when the menu opens: its first row.</summary>
    public void FocusFirst(bool keyboard) =>
        OverlayToggle.Focus(keyboard ? FocusState.Keyboard : FocusState.Programmatic);

    // ── Flyouts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A row with a flyout: resting on it opens the flyout after a beat, a click opens
    /// or closes it at once, and Right (or Enter / Space) opens it with focus inside.
    /// </summary>
    private void Sub(ToggleButton row, StackPanel list)
    {
        _lists[row] = list;
        row.Click += (_, _) =>
        {
            var keyboard = row.FocusState == FocusState.Keyboard;
            if (_openRow == row && !keyboard) CloseFlyout();
            else OpenFlyout(row, focus: keyboard);
            CancelHover();
        };
        row.PointerEntered += (_, _) =>
        {
            if (_openRow == row) CancelHover();
            else Hover(220, () => OpenFlyout(row, focus: false));
        };
        row.PointerExited += (_, _) =>
        {
            if (_openRow != row) CancelHover();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Right) return;
            e.Handled = true;
            OpenFlyout(row, focus: true);
        };
    }

    private void OpenFlyout(ToggleButton row, bool focus)
    {
        if (!row.IsEnabled) return;
        var list = _lists[row];
        var reopen = _openRow != row;
        if (_openRow is { } prev && prev != row) prev.IsChecked = false;
        _openRow = row;
        row.IsChecked = true;
        foreach (var l in _lists.Values) l.Visibility = l == list ? Visibility.Visible : Visibility.Collapsed;

        // Beside the row, its first choice level with it; to the right when it fits,
        // otherwise to the left (the tray is usually at the screen's right end).
        FlyoutCard.Measure(new Size(FlyoutCard.Width, double.PositiveInfinity));
        var h = FlyoutCard.DesiredSize.Height;
        var w = FlyoutCard.Width;
        var top = row.TransformToVisual(Root).TransformPoint(new Point(0, 0)).Y - 5;
        var right = Root.ActualWidth + FlyoutGap + w <= WorkArea.Right;
        var x = right ? Root.ActualWidth + FlyoutGap : -(w + FlyoutGap);
        var y = Math.Max(WorkArea.Top, Math.Min(top, WorkArea.Bottom - h));
        Flyout.HorizontalOffset = x;
        Flyout.VerticalOffset = y;

        if (!Flyout.IsOpen || reopen)
        {
            Flyout.IsOpen = true;
            if (Motion.Reduced) Motion.Fade(FlyoutCard, 1, 100, 0);
            else
            {
                Motion.Fade(FlyoutCard, 1, 120, 0);
                Motion.Slide(FlyoutCard, "Translation.X", 0, TimeSpan.FromMilliseconds(160), from: right ? -6 : 6);
            }
        }
        if (focus) FirstChoice(list)?.Focus(FocusState.Keyboard);
    }

    /// <summary>The chosen option of a list, else its first enabled control.</summary>
    private Control? FirstChoice(StackPanel list)
    {
        var option = (Style)Resources["MenuOption"];
        return list.Children.OfType<ToggleButton>().FirstOrDefault(t => t.Style == option && t.IsChecked == true && t.IsEnabled)
            ?? list.Children.OfType<Control>().FirstOrDefault(c => c.IsEnabled && c.Visibility == Visibility.Visible);
    }

    public void CloseFlyout()
    {
        CancelHover();
        if (_openRow is { } row) row.IsChecked = false;
        _openRow = null;
        Flyout.IsOpen = false;
    }

    private void Hover(int ms, Action act)
    {
        _hover.Stop();
        _hoverAction = act;
        _hover.Interval = TimeSpan.FromMilliseconds(ms);
        _hover.Start();
    }

    private void CancelHover()
    {
        _hover.Stop();
        _hoverAction = null;
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    private void OnMenuKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        if (_openRow is { } row)
        {
            CloseFlyout();
            row.Focus(FocusState.Keyboard);
        }
        else CloseRequested?.Invoke();
    }

    /// <summary>Esc or Left in a flyout closes it and returns to its row.</summary>
    private void OnFlyoutKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Escape or VirtualKey.Left)) return;
        e.Handled = true;
        var row = _openRow;
        CloseFlyout();
        row?.Focus(FocusState.Keyboard);
    }
}
