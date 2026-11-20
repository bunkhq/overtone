using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Overtone.Core;
using Overtone.Media;
using Overtone.Overlay;
using Windows.Graphics;
using WinRT.Interop;

namespace Overtone.Platform;

/// <summary>
/// The window that holds the tray's control panel (<see cref="TrayPanelView"/>). It
/// opens beside the tray, against whichever screen edge the taskbar is on, takes
/// focus so the keyboard reaches it, and closes when it loses focus or on Esc.
/// Opening and closing fade the whole window (Windows draws its frame and shadow,
/// which a content fade can't reach) while the content rises 8 px from the taskbar.
/// </summary>
public sealed class TrayPanel : Window
{
    private const double PanelWidth = 320;

    private readonly TrayPanelView _view = new();
    private readonly nint _hwnd;
    private readonly NativeAcrylicBackdrop _acrylic = new();
    private bool _open, _native;
    /// <summary>Set while opening: focus the first control on activation (true: with the keyboard ring).</summary>
    private bool? _focusOnActivate;
    private DateTime _closedAt;

    // Where the menu is anchored, as a context menu is: the click (as a 1 px rectangle
    // the menu opens beside), and Windows' alignment.
    private Win32.POINT _anchor;
    private Win32.RECT _exclude;
    private uint _flags;

    public TrayPanel(PanelHost host)
    {
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
        Win32.MakePanel(_hwnd);
        Motion.EnableTranslation(_view);

        _view.Bind(host);
        _view.CloseRequested += ClosePanel;
        _view.HeightChanged += () => { if (_open && AppWindow.IsVisible) Place(); };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // A flyout is a window of its own: focus moving into it isn't leaving the menu.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_view.FlyoutOpen && Win32.ForegroundIsOurs()) WatchFocus();
                    else ClosePanel();
                });
                return;
            }
            // XAML takes keyboard focus only once the window is really active: focusing
            // earlier is silently lost, and Esc, Space and Tab would go nowhere.
            if (_focusOnActivate is { } keyboard)
            {
                _focusOnActivate = null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    Win32.FocusContent(_hwnd);
                    _view.FocusFirst(keyboard);
                });
            }
        };
    }

    public void SetTrack(TrackInfo? track, bool playing) => _view.SetTrack(track, playing);

    public void Refresh() => _view.Refresh();

    public void SetBackdrop(byte[]? bgra, int size) => _view.SetBackdrop(bgra, size);

    /// <summary>The overlay's look: tinted Acrylic in the native look, the palette's own ground otherwise.</summary>
    public void SetLook(bool native, OtPalette palette)
    {
        // Cross-fades with the overlay while open; snaps while hidden (nothing to see).
        if (!palette.Equals(_view.Theme.Current)) _view.Theme.Apply(palette, animate: _open);
        _acrylic.SetTint(palette.Base, palette.IsLight);
        if (native == _native && (SystemBackdrop is not null) == native) return;
        _native = native;
        SystemBackdrop = native ? _acrylic : null;
        _view.SetNative(native);
    }

    /// <summary>
    /// Right-click on the tray icon (<paramref name="icon"/>: its rectangle, when Windows
    /// reports it), or the shortcut (<paramref name="atTray"/>: placed at the icon, or the
    /// tray's end of the primary taskbar, rather than at the cursor). A click that just
    /// closed the menu (by taking focus away) doesn't reopen it.
    /// </summary>
    internal void Toggle(Win32.RECT? icon = null, bool atTray = false)
    {
        if (_open) ClosePanel();
        else if (DateTime.UtcNow - _closedAt > TimeSpan.FromMilliseconds(300)) Open(icon, atTray);
    }

    /// <summary>
    /// Right-click on the overlay: opens at the cursor, or moves there if already open
    /// (the overlay never takes focus, so the menu is still open after a click on it).
    /// </summary>
    internal void OpenAtCursor()
    {
        if (!_open)
        {
            Open(null, atTray: false);
            return;
        }
        Win32.GetCursorPos(out _anchor);
        _exclude = new Win32.RECT { Left = _anchor.X, Top = _anchor.Y, Right = _anchor.X + 1, Bottom = _anchor.Y + 1 };
        _view.CloseFlyout();
        Place();
    }

    /// <summary>Closes the menu if it's open (a click on the overlay).</summary>
    internal void Dismiss() => ClosePanel();

    private void Open(Win32.RECT? icon, bool atTray)
    {
        Win32.GetCursorPos(out var c);
        _anchor = !atTray ? c
            : icon is { } ir ? new Win32.POINT { X = (ir.Left + ir.Right) / 2, Y = (ir.Top + ir.Bottom) / 2 }
            : Win32.TrayCorner() ?? c;
        var area = DisplayArea.GetFromPoint(new PointInt32(_anchor.X, _anchor.Y), DisplayAreaFallback.Nearest);
        RectInt32 w = area.WorkArea, o = area.OuterBounds;
        var edge = w.Y > o.Y ? 'T' : w.X > o.X ? 'L' : w.X + w.Width < o.X + o.Width ? 'R' : 'B';

        // Exactly as Windows places a context menu (TrackPopupMenu): a corner at the
        // click, opening down and to the right (left from a right-hand taskbar), flipped
        // up or left only when the work area has no room that way.
        _exclude = new Win32.RECT { Left = _anchor.X, Top = _anchor.Y, Right = _anchor.X + 1, Bottom = _anchor.Y + 1 };
        _flags = Win32.TPM_VERTICAL | (edge == 'R' ? Win32.TPM_RIGHTALIGN : 0);

        _open = true;
        // Opened from the keyboard, focus shows its ring at once; from a click it doesn't.
        _focusOnActivate = atTray;
        // Shown first, fully transparent, on the anchor's monitor, so layout and DPI are
        // that monitor's; then sized, placed and faded in.
        StartFade(show: true);
        AppWindow.Move(new PointInt32(w.X, w.Y));
        AppWindow.Show(true);
        Activate();
        Win32.SetForegroundWindow(_hwnd);
        _view.Reset();
        _view.Refresh();
        var from = Place();
        if (!Motion.Reduced)
        {
            Motion.Slide(_view, "Translation.Y", 0, TimeSpan.FromMilliseconds(200), from: from.Y);
            Motion.Slide(_view, "Translation.X", 0, TimeSpan.FromMilliseconds(200), from: from.X);
        }
    }

    /// <summary>
    /// Sizes the window to the menu's content and places it at the anchor. Returns the
    /// offset the menu slides in from: 8 px from the click's side.
    /// </summary>
    private (float X, float Y) Place()
    {
        var scale = Win32.Scale(_hwnd);
        var height = _view.MeasureHeight(PanelWidth);
        int width = (int)Math.Round(PanelWidth * scale), tall = (int)Math.Round(height * scale);
        var ex = _exclude;
        var r = Win32.PopupPosition(_anchor, width, tall, _flags, ex);
        AppWindow.MoveAndResize(new RectInt32(r.Left, r.Top, width, tall));
        // The work area in the window's own DIPs, so a flyout opens on the side it fits.
        var w = DisplayArea.GetFromPoint(new PointInt32(_anchor.X, _anchor.Y), DisplayAreaFallback.Nearest).WorkArea;
        _view.WorkArea = new Windows.Foundation.Rect((w.X - r.Left) / scale, (w.Y - r.Top) / scale, w.Width / scale, w.Height / scale);
        return r.Top >= ex.Bottom ? (0, -8) : r.Bottom <= ex.Top ? (0, 8) : r.Right <= ex.Left ? (8, 0) : r.Left >= ex.Right ? (-8, 0) : (0, 8);
    }

    /// <summary>
    /// While focus sits in a flyout the menu's window is inactive and gets no further
    /// deactivation, so a click elsewhere is caught by watching the foreground window.
    /// </summary>
    private void WatchFocus()
    {
        _watch ??= CreateWatch();
        _watch.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watch;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateWatch()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(100);
        t.Tick += (_, _) =>
        {
            if (!_open || Win32.GetForeground() == _hwnd) t.Stop(); // back in the menu: activation tracks it again
            else if (!Win32.ForegroundIsOurs() || !_view.FlyoutOpen)
            {
                t.Stop();
                ClosePanel();
            }
        };
        return t;
    }

    private void ClosePanel()
    {
        if (!_open) return;
        _open = false;
        _closedAt = DateTime.UtcNow;
        _watch?.Stop();
        // The flyout's window isn't part of this one's fade.
        _view.CloseFlyout();
        StartFade(show: false);
    }

    // ── Whole-window fade ──────────────────────────────────────────────────

    private bool _fading, _fadeIn;
    private double _alpha, _fadeFrom;
    private DateTime _fadeStart;

    private void StartFade(bool show)
    {
        if (show && !_fading) _alpha = 0;
        _fadeIn = show;
        _fadeFrom = _alpha;
        _fadeStart = DateTime.UtcNow;
        Win32.SetAlpha(_hwnd, (byte)Math.Round(_alpha * 255));
        if (_fading) return;
        _fading = true;
        CompositionTarget.Rendering += OnFadeFrame;
    }

    private void OnFadeFrame(object? sender, object e)
    {
        // In 150 ms, out 100 ms: a panel opened many times a day shouldn't make anyone wait.
        var duration = _fadeIn ? 0.15 : 0.1;
        var t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalSeconds / duration, 0, 1);
        var k = 1 - Math.Pow(1 - t, 3);
        _alpha = _fadeFrom + ((_fadeIn ? 1 : 0) - _fadeFrom) * k;
        Win32.SetAlpha(_hwnd, (byte)Math.Round(_alpha * 255));
        if (t < 1) return;
        _fading = false;
        CompositionTarget.Rendering -= OnFadeFrame;
        if (_fadeIn)
        {
            Win32.EndAlpha(_hwnd, keepLayered: false);
            return;
        }
        AppWindow.Hide();
    }
}
