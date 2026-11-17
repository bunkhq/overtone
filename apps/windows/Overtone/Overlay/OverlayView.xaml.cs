using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Overtone.Core;
using Overtone.Media;
using Overtone.Platform;
using Windows.Foundation;
using Windows.UI;

namespace Overtone.Overlay;

/// <summary>
/// The overlay's content: one card, three layouts (pill, card, lyric panel), all
/// coloured by <see cref="PaletteTheme"/>. Owns playback presentation: progress
/// steps about a pixel at a time, lyrics advance on one rescheduled timer.
/// </summary>
public sealed partial class OverlayView : UserControl
{
    /// <summary>Size presets (the tray's Pill / Player card / Lyrics), in DIPs. From any of them the user resizes freely.</summary>
    public static readonly IReadOnlyDictionary<OverlayMode, Size> Presets = new Dictionary<OverlayMode, Size>
    {
        [OverlayMode.Pill] = new(400, 48),
        [OverlayMode.Card] = new(340, 150),
        [OverlayMode.Lyrics] = new(360, 232),
    };

    public const double CardMinWidth = 280, CardMaxWidth = 720, CardMinHeight = 44, CardMaxHeight = 600;

    /// <summary>The card's height picks its layout: a pill below CardFrom, the player card below LyricsFrom, then the lyric panel.</summary>
    private const double CardFrom = 124, LyricsFrom = 200;

    /// <summary>The lyric panel folded to its footer row when a song has no lyrics: 12 + 34 + 12.</summary>
    private const double FoldedLyricsHeight = 66;

    /// <summary>The player card's lyric row: an 18 px line plus its 6 px gap.</summary>
    private const double CardLyricRow = 24;

    /// <summary>From this height the pill shows the song above the lyric (two lines, with room to breathe).</summary>
    private const double TwoLinePillFrom = 78;

    /// <summary>Least space between a tall pill's content and its top and bottom edges.</summary>
    private const double TallPillInset = 11;

    /// <summary>
    /// Narrowest a pill's text column may get before the layout gives it room: the
    /// controls move under the text and then drop to play/pause alone, rather than
    /// the song and lyric being cut to a word or two.
    /// </summary>
    private const double MinPillText = 150;

    /// <summary>
    /// Height of a tall pill's content for lyric size <paramref name="font"/>: the song
    /// line (12.5 px) and its 2 px gap, the lyric line, a 5 px gap, the 28 px controls row.
    /// </summary>
    private static double TallPillContent(double font) => 17 + 2 + Math.Ceiling(font * 1.4) + 5 + 28;

    public static OverlayMode LayoutFor(double height) =>
        height < CardFrom ? OverlayMode.Pill : height < LyricsFrom ? OverlayMode.Card : OverlayMode.Lyrics;

    /// <summary>
    /// Card corner radius for a layout at height <paramref name="h"/>. A pill's corners grow
    /// with its height (14 at 48 px, 26 at the tallest), so a big cover inside it stays concentric.
    /// </summary>
    private static float Radius(OverlayMode m, double h) =>
        NativeCorners ? 8 : m == OverlayMode.Pill ? (float)Math.Clamp(Math.Round(h * 0.29), 14, 26) : 16;

    /// <summary>In native look the card's corners are Windows 11's (8 px, drawn by DWM).</summary>
    private static bool NativeCorners;

    /// <summary>Gap between the pill's edge and its cover (the pill's padding).</summary>
    private const double PillInset = 6;

    /// <summary>
    /// Transparent room around the card for its shadow. The ambient layer (blur 20,
    /// y+6) needs all of it to fade to nothing; a smaller margin cuts its tail off.
    /// </summary>
    public static Thickness Pad { get; private set; } = DrawnPad;

    private static readonly Thickness DrawnPad = new(20, 14, 20, 26);

    /// <summary>Native look: Windows draws the frame, so the window is the card (no shadow margin).</summary>
    private bool _native;

    private const double MinRowHeight = 30;

    private readonly NowPlayingService _media;
    private readonly LyricsService _lyrics;
    private readonly PaletteTheme _theme = new();
    private readonly DispatcherQueueTimer _lineTimer;
    private readonly DispatcherQueueTimer _ticker;
    private readonly List<LyricRow> _rows = [];
    private readonly ImageBrush _art = (ImageBrush)Application.Current.Resources["ArtBrush"];

    private TrackInfo? _track;
    private TimelineInfo? _timeline;
    private PlaybackClock? _clock;
    private LyricsDoc _doc = LyricsDoc.NotFound;
    private OverlayMode _mode = OverlayMode.Pill;
    private int _line = int.MinValue;
    private double _offset = -1;
    private bool _pillFront = true;
    private bool _backAFront = true;
    private bool _shown;
    private bool _entrance;
    private LyricRow? _breathing;
    private OverlayBackground _background = OverlayBackground.Cover;
    private OverlayTone _tone = OverlayTone.Auto;
    private Artwork? _artwork;

    private Compositor? _compositor;
    private CompositionRoundedRectangleGeometry? _cardShape;

    // Lyric fade: LyricSource renders into a visual surface, painted through a vertical alpha gradient.
    private const double LyricFadeTop = 12, LyricFadeBottom = 16, LyricOverhang = 8;
    private EdgeFade? _pillFade, _cardFade;
    private CompositionVisualSurface? _lyricSurface;
    private SpriteVisual? _lyricSprite;
    private CompositionColorGradientStop? _fadeTopEdge, _fadeTopIn, _fadeBottomIn, _fadeBottomEdge;
    private bool _fadeTop, _fadeBottom;

    // Seeking: the progress bars are sliders, lyric lines are links to their timestamps.
    private bool _canSeek;
    private bool _scrubbing;
    private float _scrubFrac;
    private bool _handCursor;

    // Free scrolling through the lyrics; the panel glides back to the current line after a pause.
    private static readonly TimeSpan FollowResume = TimeSpan.FromSeconds(2.5);
    private readonly DispatcherQueueTimer _followTimer;
    private bool _userScroll;
    private string? _lyricNow;

    // Size: what the user chose (DIPs), and whether the lyric space is in use.
    private double _userW = 400, _userH = 48;
    private bool _lyricsSpace = true;
    private double _lyricWidth = 332;
    private double _cardArt = 64;
    private readonly DispatcherQueueTimer _panelTimer;

    public OverlayView(NowPlayingService media, LyricsService lyrics)
    {
        _media = media;
        _lyrics = lyrics;
        InitializeComponent();
        // Composition DropShadow was tried and dropped: a LayerVisual clips its blur to
        // its bounds (a hard edge), and growing the layer turned the window opaque.
        ShadowHost.Source = new BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "shadow.png")));

        PillTransport.Size(32, 15, 10);
        CardTransport.Size(32, 16, 10);
        LyricsTransport.Size(28, 14, 8);
        PillTransportTall.Size(28, 14, 8);
        foreach (var t in new[] { PillTransport, PillTransportTall, CardTransport, LyricsTransport })
        {
            t.Previous += () => _ = _media.PreviousAsync();
            t.PlayPause += () => _ = _media.PlayPauseAsync();
            t.Next += () => _ = _media.NextAsync();
        }

        _lineTimer = DispatcherQueue.CreateTimer();
        _lineTimer.IsRepeating = false;
        _lineTimer.Tick += (_, _) => SyncLyrics(animate: Live);
        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Tick += (_, _) => Tick();
        _followTimer = DispatcherQueue.CreateTimer();
        _followTimer.IsRepeating = false;
        _followTimer.Interval = FollowResume;
        _followTimer.Tick += (_, _) => ResumeFollow();

        _panelTimer = DispatcherQueue.CreateTimer();
        _panelTimer.IsRepeating = false;
        _panelTimer.Interval = TimeSpan.FromMilliseconds(130);
        _panelTimer.Tick += (_, _) => CollapseHiddenPanels();

        WireScrub(PillScrub, PillTrack, 2f);
        WireScrub(CardScrub, CardTrack, 1.5f);
        LyricWindow.PointerWheelChanged += OnLyricWheel;

        // Hidden layers start at composition opacity 0. XAML Opacity would multiply
        // with it, so the XAML property stays at 1 everywhere animated.
        Motion.V(BackB).Opacity = 0;
        Motion.V(PillLineB).Opacity = 0;
        // The shadow's black caster must never show without the card above it
        // (e.g. a slow cold start), so it appears and disappears with the card.
        Motion.V(ShadowHost).Opacity = 0;

        Motion.WatchOpaque(() => DispatcherQueue.TryEnqueue(ApplyBackground));

        Loaded += (_, _) => BuildComposition();
        Card.SizeChanged += (_, e) => ResizeShapes(e.NewSize);
        LyricWindow.SizeChanged += (_, e) =>
        {
            LyricWindow.Clip = new RectangleGeometry { Rect = new Rect(-LyricOverhang, 0, e.NewSize.Width + LyricOverhang * 2, e.NewSize.Height) };
            ResizeLyricMask(e.NewSize);
            // Wider or narrower: rows re-wrap. Taller or shorter: the window re-places itself.
            if (Math.Abs(e.NewSize.Width - _lyricWidth) > 0.5 && e.NewSize.Width > 0)
            {
                _lyricWidth = e.NewSize.Width;
                RelayoutRows();
            }
            else if (_rows.Count > 0)
            {
                ApplyOffset(_userScroll ? Math.Clamp(_offset, 0, LyricMaxOffset) : FollowOffset(_line), animate: false);
            }
        };
        // Clip the pill's line so a scrolling lyric never slides under the artwork or controls.
        PillLineHost.SizeChanged += (_, e) =>
        {
            PillLineHost.Clip = new RectangleGeometry { Rect = new Rect(0, -14, e.NewSize.Width, e.NewSize.Height + 28) };
            _pillFade?.Resize(e.NewSize);
            // A resize re-aligns a line that had scrolled, and re-times its glide for the new width.
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Remarquee(_pillFront ? PillLineA : PillLineB, PillLineHost);
        };
        CardLyricHost.SizeChanged += (_, e) =>
        {
            CardLyricHost.Clip = new RectangleGeometry { Rect = new Rect(0, -6, e.NewSize.Width, e.NewSize.Height + 12) };
            _cardFade?.Resize(e.NewSize);
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Remarquee(CardLyric, CardLyricHost);
        };
        Card.DoubleTapped += OnDoubleTapped;

        _media.TrackChanged += OnTrack;
        _media.TimelineChanged += OnTimeline;
        ApplyUserSize(400, 48, animate: false);
    }

    /// <summary>Raised with false when nothing is playing anywhere, true when something is.</summary>
    public event Action<bool>? TrackPresence;

    public event Action? CycleModeRequested;

    /// <summary>The element the window listens to for drag and resize: the card plus its margin.</summary>
    public UIElement InputSurface => Root;

    /// <summary>Width of the grab band along the card's edges, in DIPs (plus the margin outside).</summary>
    private const double GripBand = 7;

    /// <summary>Which resize edges the pointer is over: X -1 left, 1 right; Y -1 top, 1 bottom; 0 none.</summary>
    public (int X, int Y) GripAt(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Card).Position;
        // Corners get a little more room, so diagonal resizing is easy to find.
        var cornerX = p.X < GripBand * 2 ? -1 : p.X > Card.ActualWidth - GripBand * 2 ? 1 : 0;
        var cornerY = p.Y < GripBand * 2 ? -1 : p.Y > Card.ActualHeight - GripBand * 2 ? 1 : 0;
        if (cornerX != 0 && cornerY != 0) return (cornerX, cornerY);
        var x = p.X < GripBand ? -1 : p.X > Card.ActualWidth - GripBand ? 1 : 0;
        var y = p.Y < GripBand ? -1 : p.Y > Card.ActualHeight - GripBand ? 1 : 0;
        return (x, y);
    }

    public void SetGripCursor((int X, int Y) grip) =>
        ProtectedCursor = InputSystemCursor.Create(grip switch
        {
            (0, 0) => _handCursor ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow,
            (0, _) => InputSystemCursorShape.SizeNorthSouth,
            (_, 0) => InputSystemCursorShape.SizeWestEast,
            var (x, y) when x == y => InputSystemCursorShape.SizeNorthwestSoutheast,
            _ => InputSystemCursorShape.SizeNortheastSouthwest,
        });

    private void SetHand(bool on)
    {
        _handCursor = on;
        SetGripCursor((0, 0));
    }

    /// <summary>What Discord Rich Presence shows. Raised on play state, seeks, track and lyric changes; null when nothing plays.</summary>
    public event Action<PresenceState?>? PresenceChanged;

    private void RaisePresence()
    {
        if (_track is null || _clock is null)
        {
            PresenceChanged?.Invoke(null);
            return;
        }
        PresenceChanged?.Invoke(new PresenceState(_track, _clock.Position, _clock.Playing, Duration, _lyricNow));
    }

    public OverlayMode Mode => _mode;

    public bool HasTrack => _track is not null;

    /// <summary>The size the user chose, in DIPs (clamped).</summary>
    public Size UserSize => new(_userW, _userH);

    /// <summary>
    /// The card's size as drawn: the user's size, folded where lyrics are missing.
    /// The player card drops its lyric line; the lyric panel folds to its footer row.
    /// </summary>
    public Size DesiredCardSize => new(_userW, _mode switch
    {
        OverlayMode.Card when !_lyricsSpace => _userH - CardLyricRow,
        OverlayMode.Lyrics when !_lyricsSpace => FoldedLyricsHeight,
        _ => _userH,
    });

    /// <summary>Raised when the drawn size changes on its own (a song with or without lyrics, a resize ending).</summary>
    public event Action? DesiredSizeChanged;

    /// <summary>
    /// Sizes the card from the height the user is dragging to (what they see), not
    /// the stored size. With lyrics they're the same. Without, the dragged edge stays
    /// under the pointer: below the card's threshold it's a pill; above it, the card
    /// without its lyric line (the stored height keeps that line's room, so it returns
    /// with the next song that has lyrics), up to the tallest such card: the lyric
    /// panel has nothing to show, so the edge stops there.
    /// </summary>
    public void ApplyDrawnSize(double w, double drawnH, bool animate)
    {
        if (_lyricsSpace)
        {
            ApplyUserSize(w, drawnH, animate);
            return;
        }
        var h = Math.Min(drawnH, LyricsFrom - CardLyricRow - 1);
        ApplyUserSize(w, h >= CardFrom - CardLyricRow ? h + CardLyricRow : h, animate);
    }

    /// <summary>Window size for a card size, in DIPs, including the shadow margin.</summary>
    public static Size WindowSize(Size card) => new(card.Width + Pad.Left + Pad.Right, card.Height + Pad.Top + Pad.Bottom);

    /// <summary>On screen with a live visual tree: the only state in which to animate.</summary>
    private bool Live => _shown && _compositor is not null;

    /// <summary>Whether the window is on screen; hidden overlays skip animation work.</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            _shown = value;
            UpdateTicker();
            UpdateBreath();
        }
    }

    // ── Composition setup ───────────────────────────────────────────────────

    private void BuildComposition()
    {
        if (_compositor is not null) return;
        _compositor = Motion.V(Card).Compositor;

        // Rounded clip for the whole card (blurred cover included).
        _cardShape = _compositor.CreateRoundedRectangleGeometry();
        Motion.V(Card).Clip = _compositor.CreateGeometricClip(_cardShape);


        foreach (var e in new UIElement[] { PillLineA, PillLineB, CardLyric, LyricColumn })
            Motion.EnableTranslation(e);
        foreach (var f in new UIElement[] { PillFill, CardFill })
            Motion.V(f).Scale = new Vector3(0, 1, 1);

        BuildLyricMask();
        _pillFade = new EdgeFade(PillLineFrame, PillLineHost);
        _pillFade.Resize(new Size(PillLineHost.ActualWidth, PillLineHost.ActualHeight));
        _cardFade = new EdgeFade(CardLyricFrame, CardLyricHost);
        _cardFade.Resize(new Size(CardLyricHost.ActualWidth, CardLyricHost.ActualHeight));
        ResizeShapes(new Size(Card.ActualWidth, Card.ActualHeight));
        if (_entrance) Appear();
        SyncAll();
    }

    /// <summary>
    /// XAML has no opacity mask, so the lyric column is redirected: LyricSource
    /// renders into a visual surface, and a sprite paints that surface through a
    /// vertical alpha gradient. A row cut by the bottom edge then fades out instead
    /// of being dropped (which left an empty band) or sliced mid-glyph.
    /// </summary>
    private void BuildLyricMask()
    {
        var c = _compositor!;
        var source = Motion.V(LyricSource);
        _lyricSurface = c.CreateVisualSurface();
        _lyricSurface.SourceVisual = source;

        var content = c.CreateSurfaceBrush(_lyricSurface);
        content.Stretch = CompositionStretch.None;
        content.HorizontalAlignmentRatio = 0;
        content.VerticalAlignmentRatio = 0;

        var fade = c.CreateLinearGradientBrush();
        fade.StartPoint = new Vector2(0, 0);
        fade.EndPoint = new Vector2(0, 1);
        _fadeTopEdge = c.CreateColorGradientStop(0, Colors.White);
        _fadeTopIn = c.CreateColorGradientStop(0, Colors.White);
        _fadeBottomIn = c.CreateColorGradientStop(1, Colors.White);
        _fadeBottomEdge = c.CreateColorGradientStop(1, Colors.White);
        foreach (var s in new[] { _fadeTopEdge, _fadeTopIn, _fadeBottomIn, _fadeBottomEdge }) fade.ColorStops.Add(s);

        var mask = c.CreateMaskBrush();
        mask.Source = content;
        mask.Mask = fade;

        _lyricSprite = c.CreateSpriteVisual();
        _lyricSprite.Brush = mask;
        _lyricSprite.Offset = new Vector3((float)-LyricOverhang, 0, 0);
        ElementCompositionPreview.SetElementChildVisual(LyricWindow, _lyricSprite);
        // The surface still captures a source whose own opacity is 0; hiding it
        // leaves only the masked copy on screen.
        source.Opacity = 0;
        ResizeLyricMask(new Size(LyricWindow.ActualWidth, LyricWindow.ActualHeight));
    }

    private void ResizeLyricMask(Size window)
    {
        if (_lyricSurface is null || _lyricSprite is null || window.Height <= 0) return;
        var size = new Vector2((float)(window.Width + LyricOverhang * 2), (float)window.Height);
        _lyricSurface.SourceSize = size;
        _lyricSprite.Size = size;
        _fadeTopIn!.Offset = (float)(LyricFadeTop / window.Height);
        _fadeBottomIn!.Offset = (float)(1 - LyricFadeBottom / window.Height);
    }

    /// <summary>Fades an edge only while a row is actually cut by it.</summary>
    private void SetLyricFade(bool top, bool bottom, bool animate)
    {
        if (_fadeTopEdge is null || _fadeBottomEdge is null) return;
        if (top != _fadeTop) FadeStop(_fadeTopEdge, top, animate);
        if (bottom != _fadeBottom) FadeStop(_fadeBottomEdge, bottom, animate);
        _fadeTop = top;
        _fadeBottom = bottom;
    }

    private void FadeStop(CompositionColorGradientStop stop, bool faded, bool animate)
    {
        var to = faded ? Color.FromArgb(0, 255, 255, 255) : Colors.White;
        stop.StopAnimation("Color");
        if (!animate || Motion.Reduced)
        {
            stop.Color = to;
            return;
        }
        var a = _compositor!.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(1, to);
        a.Duration = TimeSpan.FromMilliseconds(320);
        stop.StartAnimation("Color", a);
    }

    private void ResizeShapes(Size s)
    {
        if (_cardShape is null) return;
        var r = Radius(_mode, _userH);
        var size = new Vector2((float)s.Width, (float)s.Height);
        _cardShape.Size = size;
        _cardShape.CornerRadius = new Vector2(r);
    }

    // ── Modes ───────────────────────────────────────────────────────────────

    private FrameworkElement Panel(OverlayMode m) => m switch
    {
        OverlayMode.Card => CardPanel,
        OverlayMode.Lyrics => LyricsPanel,
        _ => PillPanel,
    };

    /// <summary>
    /// Sets the user's size. Its height picks the layout; a change of layout
    /// cross-fades (old out, new in) when <paramref name="animate"/>. Sizes inside a
    /// layout (artwork, the pill's title line) follow the size. The window then
    /// draws the card at <see cref="DesiredCardSize"/> via <see cref="SetCardSize"/>.
    /// </summary>
    public void ApplyUserSize(double w, double h, bool animate)
    {
        _userW = Math.Clamp(w, CardMinWidth, CardMaxWidth);
        _userH = Math.Clamp(h, CardMinHeight, CardMaxHeight);

        // Three pills by height: one line; a song line over the lyric; and a tall pill
        // whose controls sit under the text (beside the progress bar), so the song and
        // a larger lyric get the full width beside artwork that fills the height.
        // Two lines need real room: below TwoLinePillFrom the compact one-line pill stays,
        // rather than squeezing a song line and a lyric against the edges.
        var twoLine = _userH >= TwoLinePillFrom;
        // Text scales with the height so the lines fill the cover's height rather than
        // leaving wide gaps: the lyric is sized so the column's content takes ~85-90% of
        // the room (the rest is spacing); the song line follows at about 0.7x.
        var room = _userH - 2 * PillInset - 4;
        // The two-line pill tops out at 18 px: larger text beside the controls reads cramped,
        // and the tall pill takes over at the next step anyway.
        var twoLineFont = Math.Clamp((room * 0.85 - 9) / 2.36, 13.5, 18);
        var tallFont = Math.Clamp((room * 0.9 - 35) / 2.33, 14.5, 26);
        // The tall pill (controls under the text, the lines getting the full width) takes
        // over as soon as its content fits with TallPillInset above and below.
        var besideWidth = _userW - 2 * PillInset - Math.Clamp(_userH - 2 * PillInset, 32, 112) - 12 - 96;
        // A narrow pill moves its controls under the text a little sooner (snugger insets),
        // since beside the text they would leave it only a word or two.
        var narrow = besideWidth < MinPillText;
        var tall = TallPillContent(14.5) + 2 * (narrow ? PillInset : TallPillInset) <= _userH;
        // Still too narrow: previous and next go (play/pause stays), beside the text or
        // beside a tall pill's progress bar, whichever row is short of room.
        var tallText = _userW - 2 * PillInset - Math.Clamp(_userH - 2 * PillInset, 32, 112) - 12 - 4;
        var compact = tall ? tallText - 10 - 80 < 80 : narrow;
        PillTransport.SetCompact(compact);
        PillTransportTall.SetCompact(compact);
        if (compact && !tall) besideWidth += 64;

        // The cover fills the pill's height in every variant (inset by the padding), so no gap opens beside it.
        var pillArt = Math.Round(Math.Clamp(_userH - 2 * PillInset, 32, 112));
        PillArtColumn.Width = new GridLength(pillArt);
        PillArt.Width = PillArt.Height = pillArt;
        // Concentric with the card: inner radius = outer radius - the gap between them.
        SetCorner(PillArt, _native ? (pillArt >= 56 ? 6 : 4) : Radius(OverlayMode.Pill, _userH) - PillInset);
        var titleWas = PillTitle.Visibility;
        PillTitle.Visibility = twoLine ? Visibility.Visible : Visibility.Collapsed;
        if (titleWas != PillTitle.Visibility && _track is not null) SetPillLine(PillLineText(_lyricNow), false, MsToNextLine());
        // Half-pixel steps keep live resizing from re-laying text on every pixel.
        // Beside a narrow column the two-line text also stays modest, so it can't outgrow it.
        // One line grows too, from 13.5 px at the shortest pill to exactly the size the
        // two-line pill starts at, so switching between them keeps the lyric's size.
        var widthCap = Math.Max(13.5, besideWidth / 11);
        // A tall pill's lyric is capped the same way by its own column, so a big cover in a
        // narrow card doesn't leave room for only a few huge letters.
        tallFont = Math.Min(tallFont, Math.Max(14.5, tallText / 11));
        var twoLineStart = Math.Min(Math.Clamp(((TwoLinePillFrom - 2 * PillInset - 4) * 0.85 - 9) / 2.36, 13.5, 18), widthCap);
        var oneLineFont = 13.5 + (Math.Clamp(_userH, CardMinHeight, TwoLinePillFrom) - CardMinHeight) / (TwoLinePillFrom - CardMinHeight) * (twoLineStart - 13.5);
        var lineSize = tall ? tallFont : twoLine ? Math.Min(twoLineFont, widthCap) : oneLineFont;
        lineSize = Math.Round(lineSize * 2) / 2;
        double titleSize;
        titleSize = twoLine ? Math.Round(Math.Clamp(lineSize * (tall ? 0.68 : 0.72), 11.5, 15) * 2) / 2 : 11.5;
        PillTitle.FontSize = titleSize;
        // Two lines and up: the text column spans the cover's height (song line at its top,
        // progress at its bottom); one line: a centred group.
        PillText.VerticalAlignment = twoLine ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        PillText.Margin = new Thickness(0, twoLine ? 2 : 0, 0, twoLine ? (tall ? 0 : 3) : 0);
        PillTransport.Visibility = tall ? Visibility.Collapsed : Visibility.Visible;
        PillTransportTall.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
        PillPanel.Padding = new Thickness(6, 6, tall ? 10 : 4, 6);
        // Optical centring: a lyric is mostly lowercase, so its visual weight (x-height,
        // descenders) sits below its box's centre and it reads low between the song line
        // and the progress bar. Lift it by a tenth of its size where it sits between them.
        PillLineFrame.RenderTransform = new TranslateTransform { Y = twoLine ? -Math.Round(lineSize * 0.1) : 0 };
        if (Math.Abs(PillLineA.FontSize - lineSize) > 0.1)
        {
            PillLineA.FontSize = PillLineB.FontSize = lineSize;
            PillLineHost.Height = Math.Ceiling(lineSize * 1.4);
            Remarquee(_pillFront ? PillLineA : PillLineB, PillLineHost);
        }

        _cardArt = Math.Round(Math.Clamp(_userH - 86, 40, 120));
        CardArtColumn.Width = new GridLength(_cardArt);
        CardArt.Width = CardArt.Height = _cardArt;
        // The player card's cover sits 16 px in on both sides, the same visual inset as the
        // transport glyphs on the right and bottom (8 px padding plus the buttons' own 8 px).
        // That is too far for strict concentricity (16 - 16 = 0 reads square), so the radius
        // scales with the cover's size but stays below the card's own 16.
        SetCorner(CardArt, _native ? 6 : Math.Round(Math.Clamp(_cardArt * 0.12, 6, 12)));
        if (CardTitle.Text.Length > 0) CardTitle.FontSize = FitFont(CardTitle.Text, 16, 13.5, CardTitleWidth);

        var m = LayoutFor(_userH);
        var from = Panel(_mode);
        if (m != _mode || from.Visibility != Visibility.Visible) SetLayout(m);
        else
        {
            // Same layout, new height: a pill's corner radius follows it.
            Rim.CornerRadius = new CornerRadius(Radius(m, _userH));
            ResizeShapes(new Size(Card.ActualWidth, Card.ActualHeight));
        }
        ShowPanel(Panel(m), from, animate);
    }

    private static void SetCorner(Border art, double r)
    {
        art.CornerRadius = new CornerRadius(r);
        if (art.Child is Border inner) inner.CornerRadius = new CornerRadius(r);
    }

    /// <summary>Shows one layout: the outgoing one fades out (then collapses), the new one fades in.</summary>
    private void ShowPanel(FrameworkElement to, FrameworkElement from, bool animate)
    {
        var fade = animate && Live && !Motion.Reduced && from != to && from.Visibility == Visibility.Visible;
        if (to.Visibility != Visibility.Visible || fade)
        {
            to.Visibility = Visibility.Visible;
            if (fade) Motion.Fade(to, 1, 200, 0, delayMs: 110);
            else Motion.Fade(to, 1, 0);
        }
        foreach (var p in new[] { PillPanel, CardPanel, LyricsPanel })
        {
            if (p == to || p.Visibility != Visibility.Visible) continue;
            if (fade) Motion.Fade(p, 0, 110);
            else p.Visibility = Visibility.Collapsed;
        }
        if (fade)
        {
            _panelTimer.Stop();
            _panelTimer.Start();
        }
    }

    private void CollapseHiddenPanels()
    {
        foreach (var p in new[] { PillPanel, CardPanel, LyricsPanel })
            if (p != Panel(_mode)) p.Visibility = Visibility.Collapsed;
    }

    /// <summary>Card size in DIPs; the window sets it every frame while animating or resizing.</summary>
    public void SetCardSize(double w, double h)
    {
        Card.Width = w;
        Card.Height = h;
        // The shadow image covers the card plus the whole margin around it.
        ShadowHost.Width = w + Pad.Left + Pad.Right;
        ShadowHost.Height = h + Pad.Top + Pad.Bottom;
    }

    private void SetLayout(OverlayMode m)
    {
        _mode = m;
        Rim.CornerRadius = new CornerRadius(Radius(m, _userH));
        ResizeShapes(new Size(Card.ActualWidth, Card.ActualHeight));
        UpdateTimes();
        UpdateTicker();
        UpdateBreath();
        SyncLyrics(animate: false);
    }

    /// <summary>
    /// Folds or unfolds the space lyrics take. With no lyrics, the lyric panel keeps
    /// only its footer row (no empty window, no divider) and the player card drops
    /// its lyric line; the window animates to the new size. While lyrics load the
    /// previous state holds, so a song change doesn't flicker. Resizing never unfolds
    /// it (see <see cref="ApplyDrawnSize"/>).
    /// </summary>
    private void ApplyFold()
    {
        var fold = !_lyricsSpace;
        // The footer's cover sits as far from the bottom and left edges as from the top when folded.
        LyricsPanel.Padding = new Thickness(16, fold ? 16 : 12, 12, 16);
        LyricWindow.Visibility = fold ? Visibility.Collapsed : Visibility.Visible;
        LyricsFooter.Margin = new Thickness(0, fold ? 0 : 10, 0, 0);
        LyricsFooter.Padding = new Thickness(0, fold ? 0 : 10, 0, 0);
        LyricsFooter.BorderThickness = new Thickness(0, fold ? 0 : 1, 0, 0);
        CardLyricFrame.Visibility = _lyricsSpace ? Visibility.Visible : Visibility.Collapsed;
        DesiredSizeChanged?.Invoke();
    }

    /// <summary>Fades the card (and its shadow) in.</summary>
    public void Appear()
    {
        // Before the tree is live, animations on the handout visual can be dropped
        // mid-flight and leave the card at opacity 0. Defer to BuildComposition.
        _entrance = true;
        if (_compositor is null) return;
        _entrance = false;
        var ms = Motion.Reduced ? 150 : 220;
        Motion.Fade(Card, 1, ms, 0);
        Motion.Fade(ShadowHost, 1, ms, 0);
    }

    /// <summary>Opacity of the whole overlay in ghost mode, as on macOS.</summary>
    public const double GhostOpacity = 0.55;

    /// <summary>
    /// Ghost mode's dimming, on the XAML root so it multiplies with the composition
    /// fades of Appear and Disappear rather than fighting them.
    /// </summary>
    public void SetGhost(bool ghost)
    {
        Root.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Motion.Reduced ? 150 : 200) };
        Root.Opacity = ghost ? GhostOpacity : 1;
    }

    /// <summary>Fades the card out, then runs <paramref name="then"/>.</summary>
    public void Disappear(Action then)
    {
        if (_compositor is null || !_shown)
        {
            then();
            return;
        }
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Motion.Fade(Card, 0, 160);
        Motion.Fade(ShadowHost, 0, 160);
        batch.End();
        batch.Completed += (_, _) => then();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Buttons, sliders and lyric lines have their own click meaning.
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Card; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase || d == LyricColumn || d == PillScrub || d == CardScrub) return;
        CycleModeRequested?.Invoke();
    }

    // ── Track + timeline ────────────────────────────────────────────────────

    private void OnTrack(TrackInfo? t)
    {
        if (t is null)
        {
            _track = null;
            _lineTimer.Stop();
            TrackPresence?.Invoke(false);
            RaisePresence();
            return;
        }

        var newSong = !t.SameSong(_track);
        _track = t;

        CardTitle.Text = LyricsTitle.Text = string.IsNullOrWhiteSpace(t.Title) ? "Untitled" : t.Title;
        CardArtist.Text = LyricsArtist.Text = t.Artist;
        UpdatePillTitle(_lyricNow);
        CardTitle.FontSize = FitFont(CardTitle.Text, 16, 13.5, CardTitleWidth);
        AutomationProperties.SetName(Card, $"Now playing: {CardTitle.Text}, {t.Artist}");

        ApplyArtwork(t.Art, newSong);

        if (newSong)
        {
            _clock?.Dispose();
            _clock = new PlaybackClock(t.DurationMs);
            if (_timeline is { } tl) _clock.Report(tl.PositionMs, tl.ReportedAtMs, tl.Playing, tl.Rate);
            SetLyrics(LyricsDoc.Loading);
            _ = LoadLyricsAsync(t);
        }

        TrackPresence?.Invoke(true);
        SyncAll();
    }

    private void OnTimeline(TimelineInfo tl)
    {
        _timeline = tl;
        if (tl.CanSeek != _canSeek)
        {
            _canSeek = tl.CanSeek;
            if (!_canSeek) SetHand(false);
        }
        if (_clock is null) return;
        // Every report corrects the clock. A big change re-anchors everything; a
        // small one still reschedules the lyric timer, so lines land on the player's time.
        if (_clock.Report(tl.PositionMs, tl.ReportedAtMs, tl.Playing, tl.Rate)) SyncAll();
        else SyncLyrics(animate: Live);
    }

    /// <summary>Seeks the player, updating the overlay at once rather than waiting for its confirmation.</summary>
    private void Seek(long positionMs)
    {
        if (_clock is null || !_canSeek) return;
        positionMs = Math.Clamp(positionMs, 0, Duration ?? long.MaxValue);
        _clock.Report(positionMs, (long)PlaybackClock.Now, _clock.Playing, _clock.Rate);
        _userScroll = false;
        _followTimer.Stop();
        SyncAll();
        _ = _media.SeekAsync(positionMs);
    }

    // ── Progress sliders ────────────────────────────────────────────────────

    /// <summary>
    /// The progress bar as a slider: hover thickens it, press or drag scrubs (the
    /// fill and elapsed time follow the pointer), release seeks. Handling the
    /// press keeps the window from starting a drag.
    /// </summary>
    private void WireScrub(Grid hit, Border track, float hoverScale)
    {
        void Hover(bool on)
        {
            if (!on && _scrubbing) return;
            SetHand(on && _canSeek && Duration is > 0);
            var v = Motion.V(track);
            v.CenterPoint = new Vector3(0, (float)track.ActualHeight / 2, 0);
            var to = new Vector3(1, on && _canSeek ? hoverScale : 1, 1);
            if (Live && !Motion.Reduced) Motion.GlideTo(track, "Scale", to, TimeSpan.FromMilliseconds(140));
            else v.Scale = to;
        }
        void ScrubTo(PointerRoutedEventArgs e)
        {
            var w = track.ActualWidth;
            if (w <= 0 || Duration is not > 0) return;
            _scrubFrac = (float)Math.Clamp(e.GetCurrentPoint(track).Position.X / w, 0, 1);
            foreach (var fill in new UIElement[] { PillFill, CardFill })
                Motion.V(fill).Scale = new Vector3(_scrubFrac, 1, 1);
            if (_mode == OverlayMode.Card) CardPos.Text = Fmt((long)(_scrubFrac * Duration!.Value));
        }
        void End(bool commit)
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            if (commit && Duration is > 0) Seek((long)(_scrubFrac * Duration!.Value));
            Hover(false);
        }

        hit.PointerEntered += (_, _) => Hover(true);
        hit.PointerExited += (_, _) => Hover(false);
        hit.PointerPressed += (_, e) =>
        {
            if (!_canSeek || Duration is not > 0 || !e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            hit.CapturePointer(e.Pointer);
            _scrubbing = true;
            ScrubTo(e);
        };
        hit.PointerMoved += (_, e) =>
        {
            if (!_scrubbing) return;
            e.Handled = true;
            ScrubTo(e);
        };
        hit.PointerReleased += (_, e) =>
        {
            if (!_scrubbing) return;
            e.Handled = true;
            hit.ReleasePointerCapture(e.Pointer);
            End(commit: true);
        };
        hit.PointerCaptureLost += (_, _) => End(commit: true);
    }

    private async Task LoadLyricsAsync(TrackInfo t)
    {
        LyricsDoc doc;
        try { doc = await _lyrics.GetAsync(t); }
        catch (Exception) { doc = new LyricsDoc(LyricsStatus.Error, false, []); }
        if (!t.SameSong(_track)) return; // the song changed while we waited
        SetLyrics(doc);
        SyncLyrics(animate: Live);
    }

    private long? Duration => _track?.DurationMs ?? _timeline?.DurationMs;

    /// <summary>Re-anchor everything: on play, pause, seek and track change.</summary>
    private void SyncAll()
    {
        if (_clock is null || _compositor is null) return;
        var playing = _clock.Playing;
        foreach (var t in new[] { PillTransport, PillTransportTall, CardTransport, LyricsTransport }) t.SetPlaying(playing);
        var known = Duration is > 0;
        PillTrack.Opacity = known ? 1 : 0;
        CardTrack.Opacity = CardPos.Opacity = CardDur.Opacity = known ? 1 : 0;
        Tick();
        SyncLyrics(animate: Live);
        UpdateTicker();
        UpdateBreath();
        RaisePresence();
    }

    /// <summary>Progress fill and elapsed time, from the interpolated clock.</summary>
    private void Tick()
    {
        if (_clock is null || Duration is not > 0 || _scrubbing) return;
        var frac = (float)Math.Clamp(_clock.Position / (double)Duration!.Value, 0, 1);
        foreach (var fill in new UIElement[] { PillFill, CardFill })
            Motion.V(fill).Scale = new Vector3(frac, 1, 1);
        UpdateTimes();
    }

    /// <summary>
    /// Progress advances in steps of about one physical pixel instead of a
    /// continuous animation. WinUI 3's compositor runs in this process, so a
    /// running animation means rendering every frame (~12% of a core measured);
    /// a step every half second or so renders one frame each time and looks the same.
    /// </summary>
    private void UpdateTicker()
    {
        var bar = _mode switch { OverlayMode.Pill => PillTrack, OverlayMode.Card => CardTrack, _ => null };
        if (!_shown || bar is null || _clock is not { Playing: true } || Duration is not > 0)
        {
            _ticker.Stop();
            return;
        }
        var px = Math.Max(1, bar.ActualWidth * (XamlRoot?.RasterizationScale ?? 1));
        var step = Duration!.Value / _clock.Rate / px;
        if (_mode == OverlayMode.Card) step = Math.Min(step, 1000); // elapsed time ticks each second
        _ticker.Interval = TimeSpan.FromMilliseconds(Math.Clamp(step, 250, 1000));
        _ticker.Start();
    }

    private void UpdateTimes()
    {
        if (_mode != OverlayMode.Card || _clock is null || _scrubbing) return;
        CardPos.Text = Fmt(_clock.Position);
        CardDur.Text = Duration is { } d ? Fmt(d) : "";
    }

    private static string Fmt(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";

    // ── Artwork + palette ───────────────────────────────────────────────────

    private void ApplyArtwork(Artwork? art, bool newSong)
    {
        if (art is null)
        {
            if (!newSong) return;
            _artwork = null;
            _art.ImageSource = null;
            Recolor(animate: _shown);
            SetBackdrop(null);
            return;
        }

        _artwork = art;
        Recolor(animate: _shown);
        _art.ImageSource = Bitmap(art.DisplayBgra, art.DisplaySize, art.DisplaySize);
        SetBackdrop(CoreLib.Backdrop(art.Rgba, art.Width, art.Height));

        // A small lift on the visible artwork as the new cover lands.
        if (Live && !Motion.Reduced)
        {
            var el = _mode switch { OverlayMode.Card => CardArt, OverlayMode.Lyrics => LyricsArt, _ => PillArt };
            Motion.V(el).CenterPoint = new Vector3((float)el.Width / 2, (float)el.Height / 2, 0);
            Motion.SpringTo(el, "Scale", Vector3.One, Motion.Morph, new Vector3(0.9f, 0.9f, 1));
        }
    }

    private void SetBackdrop(byte[]? rgba)
    {
        var (front, back) = _backAFront ? (BackA, BackB) : (BackB, BackA);
        var ms = Live ? Motion.PaletteFade.TotalMilliseconds : 0;
        if (rgba is null)
        {
            Motion.Fade(front, 0, ms);
            return;
        }
        var n = CoreLib.BackdropSize;
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        back.Source = Bitmap(bgra, n, n);
        Motion.Fade(back, 1, ms, 0);
        Motion.Fade(front, 0, ms);
        _backAFront = !_backAFront;
    }

    /// <summary>What fills the card. The OS "Transparency effects" switch turns the blurred cover into a solid fill.</summary>
    public new OverlayBackground Background
    {
        get => _background;
        set
        {
            _background = value;
            ApplyBackground();
        }
    }

    /// <summary>Dark or light card; Auto lets the palette engine decide from the cover.</summary>
    public OverlayTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            if (value == OverlayTone.System && _systemTheme is null)
            {
                // Follow light/dark switches live; the event arrives off the UI thread.
                _systemTheme = new Windows.UI.ViewManagement.UISettings();
                _systemTheme.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_tone == OverlayTone.System) Recolor(animate: Live);
                });
            }
            Recolor(animate: Live);
        }
    }

    private void Recolor(bool animate)
    {
        var force = _tone switch { OverlayTone.Dark => 0, OverlayTone.Light => 1, OverlayTone.System => SystemDark() ? 0 : 1, _ => -1 };
        var p = _artwork is { } a ? CoreLib.Palette(a.Rgba, a.Width, a.Height, force) : CoreLib.Palette([], 0, 0, force);
        _theme.Apply(p, animate);
        ThemeApplied?.Invoke(p);
        // The tray icon follows the cover; with no artwork it returns to the brand colours.
        PaletteChanged?.Invoke(_artwork is null ? null : p);
    }

    private Windows.UI.ViewManagement.UISettings? _systemTheme;

    /// <summary>Windows' app mode: the system background colour is dark in dark mode.</summary>
    private static bool SystemDark()
    {
        var bg = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        return bg.R + bg.G + bg.B < 384;
    }

    /// <summary>The palette now in use (a neutral one without artwork); native look tints its Acrylic with it.</summary>
    public event Action<OtPalette>? ThemeApplied;

    /// <summary>
    /// Native Windows look: the card turns transparent over the window's Acrylic, Windows
    /// draws the frame and shadow (so no shadow margin), corners follow Windows 11, and the
    /// controls use Fluent icons without the cursor-following shine. Off: the drawn card.
    /// </summary>
    public void ApplyLook(bool native)
    {
        _native = NativeCorners = native;
        Pad = native ? new Thickness(0) : DrawnPad;
        Root.Padding = Pad;
        GlowButton.Native = native;
        foreach (var t in new[] { PillTransport, PillTransportTall, CardTransport, LyricsTransport }) t.SetNative(native);
        SetCorner(LyricsArt, native ? 4 : 7);
        ShadowHost.Visibility = native ? Visibility.Collapsed : Visibility.Visible;
        ApplyBackground();
        ApplyUserSize(_userW, _userH, animate: false); // re-derives corner radii and artwork
        Rim.CornerRadius = new CornerRadius(Radius(_mode, _userH));
        ResizeShapes(new Size(Card.ActualWidth, Card.ActualHeight));
    }

    /// <summary>The current track's palette, or null without artwork (the tray icon follows it).</summary>
    public event Action<OtPalette?>? PaletteChanged;

    private void ApplyBackground()
    {
        if (_native)
        {
            // The window's Acrylic (tinted with the base colour) is the background.
            BaseFill.Visibility = BackA.Visibility = BackB.Visibility = Scrim.Visibility = Tint.Visibility = Rim.Visibility = Visibility.Collapsed;
            return;
        }
        BaseFill.Visibility = Rim.Visibility = Visibility.Visible;
        var style = _background == OverlayBackground.Cover && Motion.Opaque ? OverlayBackground.Solid : _background;
        var cover = style == OverlayBackground.Cover ? Visibility.Visible : Visibility.Collapsed;
        BackA.Visibility = BackB.Visibility = Scrim.Visibility = cover;
        // Solid shows the base rectangle beneath everything.
        Tint.Visibility = style == OverlayBackground.Gradient ? Visibility.Visible : Visibility.Collapsed;
    }

    private static WriteableBitmap Bitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(w, h);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(bgra, 0, Math.Min(bgra.Length, w * h * 4));
        wb.Invalidate();
        return wb;
    }

    // ── Lyrics ──────────────────────────────────────────────────────────────

    private sealed class LyricRow
    {
        public required Grid Root;
        public TextBlock? Faint, Now;
        public Ellipse[]? Dots;
        public Storyboard? Breath;
        public bool Visible, Current;
        public double Top, Height;
    }

    private void SetLyrics(LyricsDoc doc)
    {
        _doc = doc;
        // Fold the lyric space for songs without lyrics; hold the current state while loading.
        if (doc.Status != LyricsStatus.Loading && doc.HasLines != _lyricsSpace)
        {
            _lyricsSpace = doc.HasLines;
            ApplyFold();
        }
        _line = int.MinValue;
        _offset = -1;
        _userScroll = false;
        _followTimer.Stop();
        _breathing?.Breath?.Stop();
        _breathing = null;
        _rows.Clear();
        LyricColumn.Children.Clear();
        if (_compositor is not null) Motion.Set(Motion.V(LyricColumn), "Translation", Vector3.Zero);

        LyricStatus.Visibility = doc.HasLines ? Visibility.Collapsed : Visibility.Visible;
        LyricStatus.Text = doc.Status switch
        {
            LyricsStatus.Loading => "Finding lyrics…",
            LyricsStatus.Instrumental => "Instrumental",
            LyricsStatus.Error => "Lyrics are unavailable right now",
            _ => "No lyrics for this track",
        };
        if (!doc.HasLines) return;

        // Rows wrap, so each is measured once and placed at a fixed offset on a
        // Canvas: collapsing far-away rows never reflows the rest, and only the
        // rows near the window stay in the render tree.
        var top = 0.0;
        foreach (var line in doc.Lines)
        {
            var row = MakeRow(line);
            row.Top = top;
            top += row.Height;
            Canvas.SetTop(row.Root, row.Top);
            row.Root.Visibility = row.Top < LyricH * 2 ? Visibility.Visible : Visibility.Collapsed;
            _rows.Add(row);
            LyricColumn.Children.Add(row.Root);
        }
    }

    private LyricRow MakeRow(LyricLine line)
    {
        var root = new Grid { Width = _lyricWidth };
        Motion.V(root).Opacity = 0;
        var row = new LyricRow { Root = root, Height = MinRowHeight };
        if (line.IsGap)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            row.Dots = new Ellipse[3];
            var sb = new Storyboard();
            for (var i = 0; i < 3; i++)
            {
                var dot = row.Dots[i] = new Ellipse { Width = 6, Height = 6, Fill = Res("InkFaintBrush") };
                dots.Children.Add(dot);
                var a = new DoubleAnimation
                {
                    From = 1, To = 0.35, Duration = TimeSpan.FromMilliseconds(800), AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 200),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(a, dot);
                Storyboard.SetTargetProperty(a, "Opacity");
                sb.Children.Add(a);
            }
            row.Breath = sb;
            AutomationProperties.SetName(dots, "Instrumental");
            root.Children.Add(dots);
        }
        else
        {
            // A synced line is a link to its timestamp: a soft wash on hover, click to seek.
            // The wash fades through its composition visual: XAML opacity transitions
            // don't reach the masked copy of the column that is actually on screen.
            var wash = new Border
            {
                CornerRadius = new CornerRadius(8), Background = Res("HoverBrush"),
                Margin = new Thickness(-LyricOverhang + 2, 0, -LyricOverhang + 2, 0),
            };
            Motion.V(wash).Opacity = 0;
            root.Children.Add(wash);
            root.Background = new SolidColorBrush(Colors.Transparent); // whole row hit-testable
            root.PointerEntered += (_, _) =>
            {
                if (!LinesSeekable) return;
                Motion.Fade(wash, 1, 120);
                SetHand(true);
            };
            root.PointerExited += (_, _) =>
            {
                Motion.Fade(wash, 0, 160);
                SetHand(false);
            };
            root.Tapped += (_, e) =>
            {
                if (!LinesSeekable) return;
                e.Handled = true;
                Motion.Fade(wash, 0, 160);
                Seek(line.StartMs);
            };

            row.Faint = LyricText(line.Text, "InkFaintBrush");
            row.Now = LyricText(line.Text, "TitleBrush");
            root.Children.Add(row.Faint);
            root.Children.Add(row.Now);
            // Measured at full size: the current line is the one that must fit.
            row.Faint.Measure(new Size(_lyricWidth, double.PositiveInfinity));
            row.Height = Math.Max(MinRowHeight, Math.Ceiling(row.Faint.DesiredSize.Height) + 8);
        }
        root.Height = row.Height;
        var v = Motion.V(root);
        v.CenterPoint = new Vector3(0, (float)row.Height / 2, 0);
        v.Scale = new Vector3(0.8f, 0.8f, 1);
        if (row.Now is not null) Motion.V(row.Now).Opacity = 0;
        return row;
    }

    /// <summary>Wraps to as many lines as it needs (three at most, which always fits the window).</summary>
    private static TextBlock LyricText(string text, string brush) => new()
    {
        Text = text, FontSize = 17, LineHeight = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Res(brush),
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 3,
        TextTrimming = TextTrimming.CharacterEllipsis, CharacterSpacing = -10,
    };

    /// <summary>Room for the player card's title beside its artwork.</summary>
    private double CardTitleWidth => Math.Max(60, _userW - 12 - 8 - 4 - _cardArt - 12);

    /// <summary>Shrinks long lines to fit (down to <paramref name="min"/>) before falling back to an ellipsis.</summary>
    private static double FitFont(string text, double max, double min, double width)
    {
        var probe = new TextBlock { Text = text, FontSize = max, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = -10 };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = probe.DesiredSize.Width;
        return natural <= width ? max : Math.Max(min, Math.Floor(max * width / natural * 2) / 2);
    }

    private void SyncLyrics(bool animate)
    {
        _lineTimer.Stop();
        if (_clock is null || _track is null || _compositor is null) return;
        var pos = _clock.Position;
        var dur = Duration;
        long? wait = _clock.Playing && _doc.NextChangeMs(pos, dur) is { } next ? _clock.WallMsUntil(next) : null;
        ShowLine(_doc.LineAt(pos, dur), animate, wait);

        if (wait is { } ms)
        {
            _lineTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms + 8));
            _lineTimer.Start();
        }
    }

    /// <summary>The lyric window's height; before layout (or while folded) the height it will have.</summary>
    private double LyricH => LyricWindow.ActualHeight > 1 ? LyricWindow.ActualHeight : Math.Max(40, _userH - 79);

    /// <summary>Re-wraps every row at the current width (after a resize) and re-places the window.</summary>
    private void RelayoutRows()
    {
        var top = 0.0;
        foreach (var r in _rows)
        {
            r.Root.Width = _lyricWidth;
            if (r.Faint is not null)
            {
                r.Faint.Measure(new Size(_lyricWidth, double.PositiveInfinity));
                r.Height = Math.Max(MinRowHeight, Math.Ceiling(r.Faint.DesiredSize.Height) + 8);
            }
            r.Root.Height = r.Height;
            Motion.V(r.Root).CenterPoint = new Vector3(0, (float)r.Height / 2, 0);
            r.Top = top;
            Canvas.SetTop(r.Root, top);
            top += r.Height;
        }
        if (_rows.Count > 0) ApplyOffset(_userScroll ? Math.Clamp(_offset, 0, LyricMaxOffset) : FollowOffset(_line), animate: false);
    }

    /// <summary>Lines are clickable when they carry real timestamps and the player can seek.</summary>
    private bool LinesSeekable => _doc.Synced && _canSeek;

    private double LyricMaxOffset => _rows.Count == 0 ? 0 : Math.Max(0, _rows[^1].Top + _rows[^1].Height - LyricH);

    /// <summary>
    /// Where the window rests for line <paramref name="index"/>: the previous line on
    /// top (whole), the current line under it, then the lines that follow filling the
    /// rest. If the previous and current lines don't both fit, the current line goes
    /// to the top. Song start: the first line at the top; the end: the last line on the bottom edge.
    /// </summary>
    private double FollowOffset(int index)
    {
        var offset = 0.0;
        if (index > 0 && index < _rows.Count)
        {
            var cur = _rows[index];
            var prev = _rows[index - 1];
            offset = cur.Top + cur.Height - prev.Top <= LyricH ? prev.Top : cur.Top;
        }
        return Math.Clamp(offset, 0, LyricMaxOffset);
    }

    /// <summary>Scrolls the lyric window to <paramref name="offset"/> and updates which rows show and which edges fade.</summary>
    private void ApplyOffset(double offset, bool animate, TimeSpan? duration = null)
    {
        if (Math.Abs(offset - _offset) > 0.5)
        {
            _offset = offset;
            var to = new Vector3(0, (float)-offset, 0);
            if (animate) Motion.GlideTo(LyricColumn, "Translation", to, duration ?? Motion.Lyric);
            else Motion.Set(Motion.V(LyricColumn), "Translation", to);
        }

        // Edges fade only where a row is cut. Rows are measured with 4 px of air
        // above and below the text, so a row sitting on an edge isn't "cut".
        bool Cut(double edge) => _rows.Any(r => r.Top + 4 < edge - 0.5 && r.Top + r.Height - 4 > edge + 0.5);
        SetLyricFade(Cut(offset), Cut(offset + LyricH), animate);

        foreach (var r in _rows)
        {
            // Any row inside the window shows; the mask fades the ones cut by an edge.
            var visible = r.Top < offset + LyricH - 0.5 && r.Top + r.Height > offset + 0.5;
            // Keep rows within a window's height either side realized so they can fade as they slide.
            var near = r.Top + r.Height >= offset - LyricH && r.Top <= offset + LyricH * 2;
            r.Root.Visibility = near ? Visibility.Visible : Visibility.Collapsed;
            if (visible == r.Visible) continue;
            r.Visible = visible;
            // Rows leaving fade while the column slides them out of the clip;
            // a quicker fade would open an empty slot before the slide finishes.
            Motion.Fade(r.Root, visible ? 1 : 0, animate ? (visible ? 300 : 380) : 0);
        }
    }

    /// <summary>Mouse wheel or touchpad: scroll freely through the whole song's lyrics.</summary>
    private void OnLyricWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        e.Handled = true;
        var delta = e.GetCurrentPoint(LyricWindow).Properties.MouseWheelDelta; // 120 per notch
        var target = Math.Clamp(_offset - delta / 120.0 * 44, 0, LyricMaxOffset);
        _userScroll = true;
        ApplyOffset(target, animate: Live, TimeSpan.FromMilliseconds(180));
        _followTimer.Stop();
        _followTimer.Start();
    }

    /// <summary>After a scroll, glide back to the line that's playing.</summary>
    private void ResumeFollow()
    {
        if (!_userScroll) return;
        _userScroll = false;
        ApplyOffset(FollowOffset(_line), animate: Live, TimeSpan.FromMilliseconds(520));
    }

    private void ShowLine(int index, bool animate, long? msToNext)
    {
        // Pill: the current lyric, or the song itself between lines.
        var lyric = index >= 0 && index < _doc.Lines.Count && !_doc.Lines[index].IsGap ? _doc.Lines[index].Text : null;
        SetPillLine(PillLineText(lyric), animate, msToNext);
        UpdatePillTitle(lyric);
        if (lyric != _lyricNow)
        {
            _lyricNow = lyric;
            SetCardLyric(lyric, animate, msToNext);
            RaisePresence();
        }

        if (index == _line || _rows.Count == 0) return;
        _line = index;

        // While the user scrolls, the highlight moves but the window stays where they put it.
        if (!_userScroll) ApplyOffset(FollowOffset(index), animate);

        for (var k = 0; k < _rows.Count; k++)
        {
            var r = _rows[k];
            var current = k == index;
            if (current == r.Current) continue;
            r.Current = current;
            var scale = current ? Vector3.One : new Vector3(0.8f, 0.8f, 1);
            if (animate) Motion.GlideTo(r.Root, "Scale", scale, Motion.Lyric);
            else Motion.Set(Motion.V(r.Root), "Scale", scale);
            if (r.Now is not null) Motion.Fade(r.Now, current ? 1 : 0, animate ? 300 : 0);
            if (r.Dots is not null)
            {
                foreach (var d in r.Dots) d.Fill = Res(current ? "TitleBrush" : "InkFaintBrush");
            }
        }
        UpdateBreath();
    }

    /// <summary>The player card's lyric line: the new line fades in; between lines the last one fades away.</summary>
    private void SetCardLyric(string? lyric, bool animate, long? msToNext)
    {
        if (_compositor is null) return;
        if (lyric is null)
        {
            Motion.Fade(CardLyric, 0, animate ? 260 : 0);
            return;
        }
        CardLyric.Text = lyric;
        Motion.SetChannel(CardLyric, "Translation.X", 0);
        Motion.Fade(CardLyric, 1, animate ? 240 : 0, animate ? 0 : null);
        Marquee(CardLyric, CardLyricHost, animate, msToNext);
    }

    /// <summary>
    /// The pill's main line: the lyric; with no lyric, the song ("title · artist" in a one-line
    /// pill, just the title when the small line above already names the artist).
    /// </summary>
    private string PillLineText(string? lyric)
    {
        if (lyric is not null) return lyric;
        var artist = _track?.Artist;
        return PillTitle.Visibility == Visibility.Visible || string.IsNullOrWhiteSpace(artist) ? CardTitle.Text : $"{CardTitle.Text} · {artist}";
    }

    /// <summary>The small line above: "title · artist" over a lyric, the artist alone over the title.</summary>
    private void UpdatePillTitle(string? lyric)
    {
        var artist = _track?.Artist ?? "";
        PillTitle.Text = lyric is null ? artist : string.IsNullOrWhiteSpace(artist) ? CardTitle.Text : $"{CardTitle.Text} · {artist}";
    }

    /// <summary>Wall-clock ms until the next lyric line, for timing a glide; null when paused or unknown.</summary>
    private long? MsToNextLine()
    {
        if (_clock is not { Playing: true }) return null;
        return _doc.NextChangeMs(_clock.Position, Duration) is { } next ? _clock.WallMsUntil(next) : null;
    }

    /// <summary>Restarts a line's glide from its start (after a resize or font change).</summary>
    private void Remarquee(TextBlock line, FrameworkElement host)
    {
        if (_compositor is null || string.IsNullOrEmpty(line.Text)) return;
        // Reset both axes: rewriting only X while the line's slide-in (Y) is still
        // animating would write back a stale Y and leave the line sitting too high.
        Motion.Set(Motion.V(line), "Translation", Vector3.Zero);
        line.Width = double.NaN;
        line.TextTrimming = TextTrimming.None;
        Marquee(line, host, Live, MsToNextLine());
    }

    /// <summary>
    /// The instrumental dots breathe only when someone can see them: lyric panel
    /// on screen, the gap is the current line, and the music is playing. A looping
    /// animation keeps the in-process compositor rendering every frame.
    /// </summary>
    private void UpdateBreath()
    {
        var want = Live && _mode == OverlayMode.Lyrics && _clock is { Playing: true } && !Motion.Reduced
            ? _rows.FirstOrDefault(r => r.Current && r.Breath is not null)
            : null;
        if (want == _breathing) return;
        _breathing?.Breath?.Stop();
        _breathing = want;
        _breathing?.Breath?.Begin();
    }

    private void SetPillLine(string text, bool animate, long? msToNext)
    {
        var (cur, next) = _pillFront ? (PillLineA, PillLineB) : (PillLineB, PillLineA);
        if (cur.Text == text) return;
        next.Text = text;
        _pillFront = !_pillFront;
        if (_compositor is null) return;
        Motion.SetChannel(next, "Translation.X", 0);
        if (!animate)
        {
            Motion.SetChannel(next, "Translation.Y", 0);
            Motion.Fade(next, 1, 0);
            Motion.Fade(cur, 0, 0);
        }
        else
        {
            Motion.Slide(next, "Translation.Y", 0, Motion.Lyric, from: 10);
            Motion.Fade(next, 1, 260, 0);
            Motion.Slide(cur, "Translation.Y", -10, Motion.Lyric);
            Motion.Fade(cur, 0, 180);
        }
        Marquee(next, PillLineHost, animate, msToNext);
    }

    /// <summary>
    /// A line wider than the pill scrolls across once: a beat to start reading,
    /// then an even glide that lands on the line's end before the next one is due.
    /// </summary>
    private void Marquee(TextBlock line, FrameworkElement host, bool animate, long? msToNext)
    {
        line.Width = double.NaN;
        line.TextTrimming = TextTrimming.None;
        line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fade = host == PillLineHost ? _pillFade : host == CardLyricHost ? _cardFade : null;
        var overflow = line.DesiredSize.Width - host.ActualWidth;
        if (overflow <= 1 || host.ActualWidth <= 0)
        {
            fade?.Begin(false, animate, line);
            return;
        }
        if (!animate || Motion.Reduced)
        {
            fade?.Begin(false, false, line);
            line.Width = host.ActualWidth; // no motion: fall back to an ellipsis
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            return;
        }
        // Soft edges instead of a hard cut: the right fades while text runs past it, the
        // left once the line moves; the glide carries the end clear of the right fade.
        if (fade is not null) overflow += EdgeFade.Width;
        line.Width = double.NaN;
        line.TextTrimming = TextTrimming.None;
        const double startDelay = 900, endHold = 700, comfortable = 45, fastest = 140; // ms, ms, px/s, px/s
        var seconds = overflow / comfortable;
        if (msToNext is { } left)
            seconds = Math.Max(overflow / fastest, Math.Min(seconds, (left - startDelay - endHold) / 1000.0));
        Motion.Slide(line, "Translation.X", (float)-overflow, TimeSpan.FromSeconds(Math.Max(0.3, seconds)),
            from: 0, delay: TimeSpan.FromMilliseconds(startDelay), scroll: true);
        fade?.Begin(true, animate, line);
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public void Shutdown()
    {
        _lineTimer.Stop();
        _ticker.Stop();
        _followTimer.Stop();
        _clock?.Dispose();
        _clock = null;
    }
}
