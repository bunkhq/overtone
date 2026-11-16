using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Overtone.Platform;
using Windows.Foundation;

namespace Overtone.Overlay;

/// <summary>
/// Transport button with a soft wash and a white shine that follows the cursor.
///
/// Hover is driven here rather than by the PointerOver visual state: the overlay
/// window is never activated, and XAML doesn't always get pointer-leave when the
/// cursor exits it quickly, which left buttons stuck in hover. A short watchdog
/// clears hover once the cursor is no longer over the window.
/// </summary>
public sealed partial class GlowButton : Button
{
    private static readonly List<GlowButton> Hovered = [];

    /// <summary>Native look: a plain hover fill, as Windows draws it, without the following shine.</summary>
    public static bool Native { get; set; }
    private static DispatcherQueueTimer? _watchdog;

    private Microsoft.UI.Xaml.Controls.Border? _wash, _shine;
    private RadialGradientBrush? _glow;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _wash = GetTemplateChild("Wash") as Microsoft.UI.Xaml.Controls.Border;
        _shine = GetTemplateChild("Shine") as Microsoft.UI.Xaml.Controls.Border;
        _glow = _shine?.Background as RadialGradientBrush;
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        Follow(e);
        SetHover(true);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        Follow(e);
        if (!Hovered.Contains(this)) SetHover(true);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(false);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        SetHover(false);
    }

    /// <summary>Centres the shine on the cursor. Work happens only while the pointer moves.</summary>
    private void Follow(PointerRoutedEventArgs e)
    {
        if (_glow is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var p = e.GetCurrentPoint(this).Position;
        var c = new Point(Math.Clamp(p.X / ActualWidth, 0, 1), Math.Clamp(p.Y / ActualHeight, 0, 1));
        _glow.Center = c;
        _glow.GradientOrigin = c;
    }

    private void SetHover(bool on)
    {
        if (_wash is not null) _wash.Opacity = on ? 1 : 0;
        if (_shine is not null) _shine.Opacity = on && !Native ? 1 : 0;
        if (on)
        {
            if (!Hovered.Contains(this)) Hovered.Add(this);
            StartWatchdog();
        }
        else
        {
            Hovered.Remove(this);
        }
    }

    private void StartWatchdog()
    {
        if (_watchdog is null)
        {
            _watchdog = DispatcherQueue.CreateTimer();
            _watchdog.Interval = TimeSpan.FromMilliseconds(120);
            _watchdog.Tick += (_, _) => Check();
        }
        _watchdog.Start();
    }

    /// <summary>Runs only while something is hovered; stops itself once nothing is.</summary>
    private static void Check()
    {
        for (var i = Hovered.Count - 1; i >= 0; i--)
        {
            var b = Hovered[i];
            if (b.XamlRoot?.ContentIslandEnvironment is not { } env || !Win32.CursorOverWindow(Win32Interop.GetWindowFromWindowId(env.AppWindowId)))
                b.SetHover(false);
        }
        if (Hovered.Count == 0) _watchdog?.Stop();
    }
}
