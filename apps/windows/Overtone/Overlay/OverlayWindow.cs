using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Overtone.Media;
using Overtone.Platform;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;
using WinUIEx;

namespace Overtone.Overlay;

/// <summary>
/// The floating window: transparent, borderless, always on top, never focused,
/// no taskbar button. Handles drag with magnetic edge snap, free resizing from
/// any edge or corner (the card's height picks its layout), animated size
/// changes (presets, lyrics folding away), and showing itself only while
/// something is playing.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double EdgeMargin = 14;   // DIPs between a snapped card and the work-area edge
    private const double SnapDistance = 64; // DIPs within which an edge pulls the card in
    private const double DragSlop = 4;

    private readonly OverlayView _view;
    private readonly Settings _settings;
    private readonly nint _hwnd;
    private double _scale;

    private bool _userVisible, _hasTrack, _onScreen;

    /// <summary>The card's drawn size, in DIPs.</summary>
    private Size _card;

    private bool _pressed, _dragging;
    private (int X, int Y) _grip;
    private Size _pressSize, _pressDrawn;
    private RectInt32 _pressRect;
    private Win32.POINT _pressCursor;
    private PointInt32 _pressWindow;

    private bool _morphing;
    private (Size Size, Vector2? Anchor)? _queued;
    private RectInt32 _morphFrom, _morphTo;
    private DateTime _morphStart;

    private bool _moving;
    private PointInt32 _moveFrom, _moveTo;
    private DateTime _moveStart;

    /// <summary>The user's native-look choice, and whether it is in effect (no background turns it off).</summary>
    private bool _native, _frame;
    private readonly NativeAcrylicBackdrop _acrylic = new();
    private readonly TransparentTintBackdrop _drawn = new(Colors.Transparent);

    public OverlayWindow(Settings settings, NowPlayingService media, LyricsService lyrics)
    {
        _settings = settings;
        _userVisible = settings.Visible;
        _view = new OverlayView(media, lyrics)
        {
            LyricsOnly = settings.LyricsOnly,
            GhostDims = settings.GhostDims,
            TextOnlyColours = settings.TextOnlyColours,
            TextOnlyShadow = settings.TextOnlyShadow,
        };
        Content = _view;
        Title = "Overtone";

        _hwnd = WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        Win32.MakeOverlay(_hwnd);
        _scale = Win32.Scale(_hwnd);
        _view.ThemeApplied += p => _acrylic.SetTint(p.Base, p.IsLight);
        _native = settings.NativeLook;
        ApplyLookCore();

        var preset = OverlayView.Presets[settings.Mode];
        _view.ApplyUserSize(settings.Width ?? preset.Width, settings.Height ?? preset.Height, animate: false);
        _card = _view.DesiredCardSize;
        _view.SetCardSize(_card.Width, _card.Height);
        _view.Background = settings.Background;
        _view.Tone = settings.Tone;
        PlaceInitial();

        _view.TrackPresence += has =>
        {
            _hasTrack = has;
            UpdateVisibility();
        };
        _view.CycleModeRequested += CycleMode;
        _view.DesiredSizeChanged += () => { if (!_pressed) AnimateCard(_view.DesiredCardSize); };
        _view.Loaded += (_, _) => _view.XamlRoot.Changed += (_, _) => OnScaleMaybeChanged();

        var surface = _view.InputSurface;
        surface.PointerPressed += OnPressed;
        // The overlay never takes focus, so an open menu doesn't see this click as leaving it.
        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!_ghost && e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) Pressed?.Invoke();
        }), handledEventsToo: true);
        // On release, as Windows opens context menus; ghost mode lets right-clicks through.
        surface.RightTapped += (_, e) =>
        {
            if (_ghost) return;
            e.Handled = true;
            EndDrag();
            MenuRequested?.Invoke();
        };
        surface.PointerMoved += OnMoved;
        surface.PointerReleased += OnReleased;
        surface.PointerCaptureLost += (_, _) => EndDrag();
        surface.PointerExited += (_, _) => { if (!_pressed) _view.SetGripCursor((0, 0)); };

        _ghostTimer = DispatcherQueue.CreateTimer();
        _ghostTimer.Interval = TimeSpan.FromMilliseconds(40);
        _ghostTimer.Tick += (_, _) => UpdateGhostHit();
    }

    public event Action? StateChanged;

    /// <summary>The current track's palette, or null without artwork.</summary>
    public event Action<Overtone.Core.OtPalette?>? PaletteChanged
    {
        add => _view.PaletteChanged += value;
        remove => _view.PaletteChanged -= value;
    }

    /// <summary>The palette in use (neutral without artwork), for the tray panel's colours.</summary>
    public event Action<Overtone.Core.OtPalette>? ThemeApplied
    {
        add => _view.ThemeApplied += value;
        remove => _view.ThemeApplied -= value;
    }

    /// <summary>The blurred cover behind the card, for the tray panel's header; null without artwork.</summary>
    public event Action<byte[]?, int>? BackdropChanged
    {
        add => _view.BackdropChanged += value;
        remove => _view.BackdropChanged -= value;
    }

    /// <summary>What the overlay is presenting, for Discord Rich Presence.</summary>
    public event Action<PresenceState?>? PresenceChanged
    {
        add => _view.PresenceChanged += value;
        remove => _view.PresenceChanged -= value;
    }

    /// <summary>The layout the card's current size gives (pill, player card or lyric panel).</summary>
    public OverlayMode Mode => _view.Mode;

    public OverlayBackground Background
    {
        get => _settings.Background;
        set
        {
            var card = ContentRect();
            _settings.Background = value;
            _settings.Save();
            _view.Background = value;
            UpdateBehindTimer();
            // No background has no window frame either, whatever the native-look choice.
            if (_frame != NativeActive)
            {
                ApplyLookCore();
                PlaceCard(card);
            }
            StateChanged?.Invoke();
        }
    }

    public OverlayTone Tone
    {
        get => _settings.Tone;
        set
        {
            _settings.Tone = value;
            _settings.Save();
            _view.Tone = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Native Windows look (tray menu): system frame, Acrylic, Fluent icons. Off: the drawn card.</summary>
    public bool NativeLook
    {
        get => _native;
        set
        {
            if (_native == value) return;
            var card = ContentRect(); // measured with the old margin
            _native = value;
            _settings.NativeLook = value;
            _settings.Save();
            ApplyLookCore();
            PlaceCard(card);
            StateChanged?.Invoke();
        }
    }

    private bool NativeActive => _native && _settings.Background != OverlayBackground.None;

    private void ApplyLookCore()
    {
        _frame = NativeActive;
        _view.ApplyLook(_frame);
        Win32.SetNativeFrame(_hwnd, _frame);
        SystemBackdrop = _frame ? _acrylic : _drawn;
    }

    /// <summary>Lyrics only (tray menu): just the lyric, one line or several by height; hidden for songs without lyrics.</summary>
    public bool LyricsOnly
    {
        get => _settings.LyricsOnly;
        set
        {
            if (_settings.LyricsOnly == value) return;
            _settings.LyricsOnly = value;
            _view.LyricsOnly = value;
            _settings.Mode = _view.Mode;
            _settings.Save();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Whether ghost mode dims the overlay (tray menu); off, it only lets clicks through.</summary>
    public bool GhostDims
    {
        get => _settings.GhostDims;
        set
        {
            _settings.GhostDims = value;
            _settings.Save();
            _view.GhostDims = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>With no background, text contrasts with what's behind (tray menu); off, the tone decides.</summary>
    public bool MatchBehind
    {
        get => _settings.MatchBehind;
        set
        {
            _settings.MatchBehind = value;
            _settings.Save();
            UpdateBehindTimer();
            StateChanged?.Invoke();
        }
    }

    /// <summary>With no background, text in the album's colours (tray menu); off, plain black or white.</summary>
    public bool TextOnlyColours
    {
        get => _settings.TextOnlyColours;
        set
        {
            _settings.TextOnlyColours = value;
            _settings.Save();
            _view.TextOnlyColours = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>With no background, the shadow behind the text (tray menu).</summary>
    public bool TextOnlyShadow
    {
        get => _settings.TextOnlyShadow;
        set
        {
            _settings.TextOnlyShadow = value;
            _settings.Save();
            _view.TextOnlyShadow = value;
            StateChanged?.Invoke();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _behindTimer;
    private bool? _behindPending;

    /// <summary>
    /// Samples the screen around the overlay twice a second while it's on screen with no
    /// background and matching is on. Stopped, the last answer stays (no flash on the
    /// next show) unless matching is off or a background is back.
    /// </summary>
    private void UpdateBehindTimer()
    {
        var on = _settings.MatchBehind && _settings.Background == OverlayBackground.None;
        if (on && _onScreen)
        {
            if (_behindTimer is null)
            {
                _behindTimer = DispatcherQueue.CreateTimer();
                _behindTimer.Interval = TimeSpan.FromMilliseconds(500);
                _behindTimer.Tick += (_, _) => SampleBehind();
            }
            SampleBehind();
            _behindTimer.Start();
            return;
        }
        _behindTimer?.Stop();
        _behindPending = null;
        if (!on) _view.BehindLight = null;
    }

    private bool _sampling;

    /// <summary>Reads the screen on a worker thread (the copy waits on the compositor) and applies the answer here.</summary>
    private void SampleBehind()
    {
        if (_sampling) return;
        var p = AppWindow.Position;
        var s = AppWindow.Size;
        var win = new RectInt32(p.X, p.Y, s.Width, s.Height);
        var screen = DisplayArea.GetFromPoint(new PointInt32(p.X + s.Width / 2, p.Y + s.Height / 2), DisplayAreaFallback.Nearest).OuterBounds;
        var band = Px(8);
        _sampling = true;
        Task.Run(() => ScreenSample.Around(win, screen, band)).ContinueWith(t =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _sampling = false;
                if (t.IsCompletedSuccessfully && t.Result is { } l) ApplyBehind(l);
            });
        });
    }

    private void ApplyBehind(double l)
    {
        // Stopped meanwhile (matching off, a background back): ignore the late answer.
        if (!_settings.MatchBehind || _settings.Background != OverlayBackground.None) return;
        // White and black text contrast equally (WCAG) at a luminance of about 0.18.
        // Flipping needs a clear margin past it, and two samples in a row, so video or a
        // scrolling page doesn't make the text strobe; the very first answer is immediate.
        var current = _view.BehindLight;
        var light = l > 0.24 || (l >= 0.13 && (current ?? l > 0.18));
        if (light == current || current is null)
        {
            _behindPending = null;
            _view.BehindLight = light;
            return;
        }
        if (_behindPending != light)
        {
            _behindPending = light;
            return;
        }
        _behindPending = null;
        _view.BehindLight = light;
    }

    /// <summary>Right-click on the overlay (not in ghost mode): the tray menu, at the cursor.</summary>
    public event Action? MenuRequested;

    /// <summary>A left press on the overlay: an open tray menu closes, as a click elsewhere closes any menu.</summary>
    public event Action? Pressed;

    /// <summary>Whether a dropped overlay springs to the nearest screen edge (tray menu).</summary>
    public bool SnapToEdges
    {
        get => _settings.SnapToEdges;
        set
        {
            _settings.SnapToEdges = value;
            _settings.Save();
            StateChanged?.Invoke();
        }
    }

    /// <summary>User's show/hide choice from the tray. The overlay still hides itself while nothing plays.</summary>
    public bool UserVisible
    {
        get => _userVisible;
        set
        {
            _userVisible = value;
            _settings.Visible = value;
            _settings.Save();
            UpdateVisibility();
        }
    }

    /// <summary>The overlay's window handle (global hotkeys are registered on it).</summary>
    public nint Hwnd => _hwnd;

    private bool _ghost;

    /// <summary>
    /// Ghost mode (tray menu, Ctrl+Alt+Shift+G): the overlay dims and clicks pass
    /// through it, except on the transport controls, which stay usable (lyrics, the
    /// progress bar and the card itself don't take clicks). Not persisted, as on macOS:
    /// launching into a click-through overlay would look broken.
    /// </summary>
    public bool Ghost
    {
        get => _ghost;
        set
        {
            if (_ghost == value) return;
            _ghost = value;
            if (value) EndDrag();
            _overControls = false;
            Win32.SetClickThrough(_hwnd, value);
            _view.SetGhost(value);
            UpdateGhostTimer();
            StateChanged?.Invoke();
        }
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _ghostTimer;
    private bool _overControls;

    /// <summary>
    /// A click-through window gets no pointer events, so while ghost mode is on and the
    /// overlay is on screen the cursor is read every 40 ms (a position read, no
    /// rendering): over a control the window takes clicks, anywhere else they pass.
    /// </summary>
    private void UpdateGhostTimer()
    {
        if (_ghost && _onScreen) _ghostTimer.Start();
        else _ghostTimer.Stop();
    }

    private void UpdateGhostHit()
    {
        var over = false;
        if (!_morphing && Win32.GetCursorPos(out var c))
        {
            var p = AppWindow.Position;
            var s = AppWindow.Size;
            if (c.X >= p.X && c.Y >= p.Y && c.X < p.X + s.Width && c.Y < p.Y + s.Height)
            {
                var at = new Point((c.X - p.X) / _scale, (c.Y - p.Y) / _scale);
                over = _view.ControlBounds().Any(r => r.Contains(at));
            }
        }
        if (over == _overControls) return;
        _overControls = over;
        Win32.SetHitThrough(_hwnd, !over);
        _view.SetGhost(true, over);
    }

    /// <summary>The next layout's preset (tray menu, hotkey, double-click). Lyrics only has no player card.</summary>
    public void CycleMode() => SetMode(LyricsOnly
        ? (_view.Mode == OverlayMode.Lyrics ? OverlayMode.Pill : OverlayMode.Lyrics)
        : (OverlayMode)(((int)_view.Mode + 1) % 3));

    /// <summary>Resizes the card to a layout's preset size (tray menu, double-click), animated.</summary>
    /// <param name="anchor">Corner that stays put (0..1 per axis); defaults to the one nearest the screen edges.</param>
    public void SetMode(OverlayMode mode, Vector2? anchor = null)
    {
        var size = OverlayView.Presets[mode];
        _settings.Mode = mode;
        _settings.Width = size.Width;
        _settings.Height = size.Height;
        _settings.Save();
        _view.ApplyUserSize(size.Width, size.Height, animate: _onScreen);
        StateChanged?.Invoke();
        AnimateCard(_view.DesiredCardSize, anchor);
    }

    /// <summary>
    /// Animates the card to <paramref name="target"/> (DIPs), keeping the corner
    /// nearest the screen edges in place. The window grows once to hold both sizes,
    /// then follows the card's top-left every frame while the card's size springs
    /// inside it, so nothing jumps; at the end it shrinks tight around the card.
    /// </summary>
    private void AnimateCard(Size target, Vector2? anchor = null)
    {
        if (_morphing)
        {
            _queued = (target, anchor);
            return;
        }
        if (Math.Abs(target.Width - _card.Width) < 0.5 && Math.Abs(target.Height - _card.Height) < 0.5) return;

        StopMove();
        var from = ContentRect();
        var a = anchor ?? Anchor(from);
        var size = new SizeInt32(Px(target.Width), Px(target.Height));
        var x = a.X > 0.5f ? from.X + from.Width - size.Width : from.X;
        var y = a.Y > 0.5f ? from.Y + from.Height - size.Height : from.Y;
        var p = Clamp(new PointInt32(x, y), size);
        var to = new RectInt32(p.X, p.Y, size.Width, size.Height);

        if (!_onScreen || Motion.Reduced)
        {
            PlaceCard(to);
            return;
        }

        _morphing = true;
        _morphFrom = from;
        _morphTo = to;
        _morphStart = DateTime.UtcNow;
        if (_frame)
        {
            // The window is the card (Windows draws its frame), so it resizes every frame.
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnMorphFrame;
            return;
        }
        var envelope = new SizeInt32(
            Math.Max(from.Width, to.Width) + Px(8) + PadX + Px(OverlayView.Pad.Right),
            Math.Max(from.Height, to.Height) + Px(8) + PadY + Px(OverlayView.Pad.Bottom));
        AppWindow.MoveAndResize(new RectInt32(from.X - PadX, from.Y - PadY, envelope.Width, envelope.Height));
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnMorphFrame;
    }

    private void OnMorphFrame(object? sender, object e)
    {
        var t = (DateTime.UtcNow - _morphStart).TotalSeconds;
        var done = t >= Motion.Resize.SettleSeconds;
        var k = done ? 1 : Motion.Resize.At(t);
        double Lerp(int a, int b) => a + (b - a) * k;
        var x = (int)Math.Round(Lerp(_morphFrom.X, _morphTo.X));
        var y = (int)Math.Round(Lerp(_morphFrom.Y, _morphTo.Y));
        _card = new Size(Lerp(_morphFrom.Width, _morphTo.Width) / _scale, Lerp(_morphFrom.Height, _morphTo.Height) / _scale);
        _view.SetCardSize(_card.Width, _card.Height);
        if (_frame) AppWindow.MoveAndResize(new RectInt32(x, y, Px(_card.Width), Px(_card.Height)));
        else AppWindow.Move(new PointInt32(x - PadX, y - PadY));
        if (!done) return;

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMorphFrame;
        _morphing = false;
        PlaceCard(_morphTo);
        if (_queued is { } q)
        {
            _queued = null;
            AnimateCard(q.Size, q.Anchor);
        }
    }

    /// <summary>Window tight around the card at <paramref name="card"/> (screen pixels).</summary>
    private void PlaceCard(RectInt32 card)
    {
        _card = new Size(card.Width / _scale, card.Height / _scale);
        _view.SetCardSize(_card.Width, _card.Height);
        var win = WindowPx(_card);
        AppWindow.MoveAndResize(new RectInt32(card.X - PadX, card.Y - PadY, win.Width, win.Height));
        SavePosition();
    }

    public void Shutdown()
    {
        _ghostTimer.Stop();
        StopMove();
        _view.Shutdown();
        Close();
    }

    // ── Visibility ──────────────────────────────────────────────────────────

    private void UpdateVisibility()
    {
        var show = _userVisible && _hasTrack;
        if (show == _onScreen) return;
        _onScreen = show;
        UpdateGhostTimer();
        UpdateBehindTimer(); // samples at once; the answer lands as the fade-in starts
        if (show)
        {
            // Anything that changed size while hidden (a song without lyrics) lands before showing.
            var want = _view.DesiredCardSize;
            if (Math.Abs(want.Width - _card.Width) > 0.5 || Math.Abs(want.Height - _card.Height) > 0.5)
                AnimateCard(want);
            // Native look: Windows draws the frame, shadow and Acrylic, which the content's
            // fade can't reach, so the whole window fades in with it.
            if (_frame) FadeWindow(show: true, null);
            AppWindow.Show(false); // never take focus
            Win32.SetTopmost(_hwnd);
            _view.Shown = true;
            _view.Appear();
        }
        else
        {
            // Hide once both fades (content, and the window in native look) are done.
            var pending = _frame ? 2 : 1;
            void Hide()
            {
                if (--pending > 0 || _onScreen) return; // shown again during the fade
                _view.Shown = false;
                AppWindow.Hide();
            }
            if (_frame) FadeWindow(show: false, Hide);
            _view.Disappear(Hide);
        }
        StateChanged?.Invoke();
    }

    // ── Whole-window fade (native look) ─────────────────────────────────────

    private bool _fading, _fadeIn;
    private double _alpha = 1, _fadeFrom;
    private DateTime _fadeStart;
    private Action? _fadeDone;

    /// <summary>
    /// Fades the whole window through its layered alpha (in 200 ms, out 160 ms, as the
    /// content's fades), from wherever a fade the other way left it. Each frame sets one
    /// window attribute; nothing runs once it ends.
    /// </summary>
    private void FadeWindow(bool show, Action? done)
    {
        var interrupted = _fadeDone;
        _fadeDone = null;
        interrupted?.Invoke(); // a fade-out cut short still settles its bookkeeping
        if (show && !_fading) _alpha = 0; // shown from hidden: start transparent
        _fadeIn = show;
        _fadeDone = done;
        _fadeFrom = _alpha;
        _fadeStart = DateTime.UtcNow;
        Win32.SetAlpha(_hwnd, (byte)Math.Round(_alpha * 255));
        if (_fading) return;
        _fading = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFadeFrame;
    }

    private void OnFadeFrame(object? sender, object e)
    {
        var duration = _fadeIn ? (Motion.Reduced ? 0.15 : 0.22) : 0.16;
        var t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalSeconds / duration, 0, 1);
        var k = 1 - Math.Pow(1 - t, 3); // ease out
        _alpha = _fadeFrom + ((_fadeIn ? 1 : 0) - _fadeFrom) * k;
        Win32.SetAlpha(_hwnd, (byte)Math.Round(_alpha * 255));
        if (t < 1) return;

        _fading = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFadeFrame;
        // Faded in: an ordinary window again (ghost mode keeps it layered for click-through).
        if (_fadeIn) Win32.EndAlpha(_hwnd, keepLayered: _ghost);
        var done = _fadeDone;
        _fadeDone = null;
        done?.Invoke();
    }

    // ── Geometry (physical pixels) ──────────────────────────────────────────

    private int PadX => (int)Math.Round(OverlayView.Pad.Left * _scale);
    private int PadY => (int)Math.Round(OverlayView.Pad.Top * _scale);
    private int Px(double dip) => (int)Math.Round(dip * _scale);

    private SizeInt32 WindowPx(Size card)
    {
        var s = OverlayView.WindowSize(card);
        return new SizeInt32(Px(s.Width), Px(s.Height));
    }

    private RectInt32 ContentRect()
    {
        var p = AppWindow.Position;
        return new RectInt32(p.X + PadX, p.Y + PadY, Px(_card.Width), Px(_card.Height));
    }

    private static RectInt32 WorkArea(PointInt32 p) => DisplayArea.GetFromPoint(p, DisplayAreaFallback.Nearest).WorkArea;

    /// <summary>Which corner stays put when the card changes size: the one nearest the screen edges.</summary>
    private static Vector2 Anchor(RectInt32 r)
    {
        var w = WorkArea(new PointInt32(r.X + r.Width / 2, r.Y + r.Height / 2));
        var cx = r.X + r.Width / 2.0 - w.X;
        var cy = r.Y + r.Height / 2.0 - w.Y;
        return new Vector2(cx > w.Width / 2.0 ? 1 : 0, cy > w.Height / 2.0 ? 1 : 0);
    }

    private PointInt32 Clamp(PointInt32 p, SizeInt32 s)
    {
        var w = WorkArea(new PointInt32(p.X + s.Width / 2, p.Y + s.Height / 2));
        return new PointInt32(
            Math.Clamp(p.X, w.X, Math.Max(w.X, w.X + w.Width - s.Width)),
            Math.Clamp(p.Y, w.Y, Math.Max(w.Y, w.Y + w.Height - s.Height)));
    }

    /// <summary>
    /// Magnetic snap: edges within reach pull the card to a fixed margin, and the middle
    /// of each axis pulls the card's centre onto it (horizontally, vertically, or both).
    /// </summary>
    private PointInt32 Snap(RectInt32 r)
    {
        var w = WorkArea(new PointInt32(r.X + r.Width / 2, r.Y + r.Height / 2));
        int m = Px(EdgeMargin), reach = Px(SnapDistance);
        int x = r.X, y = r.Y;
        int midX = w.X + (w.Width - r.Width) / 2, midY = w.Y + (w.Height - r.Height) / 2;
        if (x - w.X < reach) x = w.X + m;
        else if (w.X + w.Width - (x + r.Width) < reach) x = w.X + w.Width - m - r.Width;
        else if (Math.Abs(x - midX) < reach) x = midX;
        if (y - w.Y < reach) y = w.Y + m;
        else if (w.Y + w.Height - (y + r.Height) < reach) y = w.Y + w.Height - m - r.Height;
        else if (Math.Abs(y - midY) < reach) y = midY;
        return Clamp(new PointInt32(x, y), new SizeInt32(r.Width, r.Height));
    }

    private void PlaceInitial()
    {
        var size = new SizeInt32(Px(_card.Width), Px(_card.Height));
        PointInt32 p;
        if (_settings.X is { } x && _settings.Y is { } y)
        {
            p = Clamp(new PointInt32(x, y), size);
        }
        else
        {
            // First run: bottom-right of the primary work area, clear of the tray.
            var w = DisplayArea.Primary.WorkArea;
            var m = Px(EdgeMargin);
            p = new PointInt32(w.X + w.Width - size.Width - m, w.Y + w.Height - size.Height - m);
        }
        var win = WindowPx(_card);
        AppWindow.MoveAndResize(new RectInt32(p.X - PadX, p.Y - PadY, win.Width, win.Height));
        UpdateAlign();
    }

    /// <summary>
    /// Lyrics only: text aligns to the third of the screen the overlay's middle is in
    /// (left, centre, right of the work area), live while it's dragged or resized.
    /// </summary>
    private void UpdateAlign()
    {
        var r = ContentRect();
        var mid = r.X + r.Width / 2;
        var w = WorkArea(new PointInt32(mid, r.Y + r.Height / 2));
        var f = (mid - w.X) / (double)Math.Max(1, w.Width);
        _view.LyricAlign = f < 1 / 3.0 ? TextAlignment.Left : f > 2 / 3.0 ? TextAlignment.Right : TextAlignment.Center;
    }

    private void SavePosition()
    {
        UpdateAlign();
        var r = ContentRect();
        _settings.X = r.X;
        _settings.Y = r.Y;
        _settings.Save();
    }

    private void OnScaleMaybeChanged()
    {
        var s = Win32.Scale(_hwnd);
        if (Math.Abs(s - _scale) < 0.001) return;
        var r = ContentRect();
        _scale = s;
        var win = WindowPx(_card);
        AppWindow.MoveAndResize(new RectInt32(r.X - PadX, r.Y - PadY, win.Width, win.Height));
    }

    // ── Drag, snap and resize ───────────────────────────────────────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Ghost mode takes clicks only on the controls, which handle their own.
        if (_ghost || _morphing || !e.GetCurrentPoint(_view.InputSurface).Properties.IsLeftButtonPressed) return;
        StopMove();
        _pressed = true;
        _dragging = false;
        _grip = _view.GripAt(e);
        _pressSize = _view.UserSize;
        _pressDrawn = _card;
        _pressRect = ContentRect();
        Win32.GetCursorPos(out _pressCursor);
        _pressWindow = AppWindow.Position;
        _view.InputSurface.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            _view.SetGripCursor(_ghost ? (0, 0) : _view.GripAt(e));
            return;
        }
        Win32.GetCursorPos(out var c);
        int dx = c.X - _pressCursor.X, dy = c.Y - _pressCursor.Y;
        if (_grip != (0, 0))
        {
            Resize(dx, dy);
            return;
        }
        if (_morphing) return;
        if (!_dragging && Math.Abs(dx) < Px(DragSlop) && Math.Abs(dy) < Px(DragSlop)) return;
        _dragging = true;
        _view.ShowBounds(true);
        AppWindow.Move(new PointInt32(_pressWindow.X + dx, _pressWindow.Y + dy));
        UpdateAlign();
    }

    /// <summary>
    /// Live resize: the dragged edges follow the pointer, the opposite ones stay put.
    /// The drawn height picks the layout as it crosses a threshold (cross-faded), and
    /// width simply reflows. No snapping. A sideways-only drag leaves the stored
    /// height alone, so a folded lyric panel stays folded.
    /// </summary>
    private void Resize(int dxPx, int dyPx)
    {
        _view.ShowBounds(true);
        var w = _pressSize.Width + _grip.X * dxPx / _scale;
        if (_grip.Y == 0) _view.ApplyUserSize(w, _pressSize.Height, animate: true);
        else _view.ApplyDrawnSize(w, _pressDrawn.Height + _grip.Y * dyPx / _scale, animate: true);
        var size = _view.DesiredCardSize;
        int pw = Px(size.Width), ph = Px(size.Height);
        var x = _grip.X < 0 ? _pressRect.X + _pressRect.Width - pw : _pressRect.X;
        var y = _grip.Y < 0 ? _pressRect.Y + _pressRect.Height - ph : _pressRect.Y;
        _card = size;
        _view.SetCardSize(size.Width, size.Height);
        var win = WindowPx(size);
        AppWindow.MoveAndResize(new RectInt32(x - PadX, y - PadY, win.Width, win.Height));
        UpdateAlign();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _view.InputSurface.ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_pressed) return;
        _pressed = false;
        _view.ShowBounds(false);
        if (_grip != (0, 0))
        {
            _grip = (0, 0);
            var u = _view.UserSize;
            _settings.Width = u.Width;
            _settings.Height = u.Height;
            _settings.Mode = _view.Mode;
            SavePosition();
            StateChanged?.Invoke();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        // Snapping off: it stays where it's dropped, only pulled back if it left the screen.
        var r = ContentRect();
        var target = _settings.SnapToEdges ? Snap(r) : Clamp(new PointInt32(r.X, r.Y), new SizeInt32(r.Width, r.Height));
        MoveTo(new PointInt32(target.X - PadX, target.Y - PadY));
    }

    /// <summary>Springs the window to <paramref name="to"/> (window coordinates) with spring.float.</summary>
    private void MoveTo(PointInt32 to)
    {
        _moveFrom = AppWindow.Position;
        _moveTo = to;
        if (Motion.Reduced || (_moveFrom.X == to.X && _moveFrom.Y == to.Y))
        {
            AppWindow.Move(to);
            SavePosition();
            return;
        }
        _moveStart = DateTime.UtcNow;
        if (!_moving)
        {
            _moving = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnMoveFrame;
        }
    }

    private void OnMoveFrame(object? sender, object e)
    {
        var t = (DateTime.UtcNow - _moveStart).TotalSeconds;
        var done = t >= Motion.Float.SettleSeconds;
        var k = done ? 1 : Motion.Float.At(t);
        AppWindow.Move(new PointInt32(
            (int)Math.Round(_moveFrom.X + (_moveTo.X - _moveFrom.X) * k),
            (int)Math.Round(_moveFrom.Y + (_moveTo.Y - _moveFrom.Y) * k)));
        if (done)
        {
            StopMove();
            SavePosition();
        }
    }

    private void StopMove()
    {
        if (!_moving) return;
        _moving = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMoveFrame;
    }
}
