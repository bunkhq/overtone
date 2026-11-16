using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI;

namespace Overtone.Overlay;

/// <summary>
/// Soft left/right edges for a single scrolling line. XAML has no opacity mask, so
/// the line's host renders into a visual surface that a sprite (a child visual of
/// the frame around it) paints through a horizontal alpha gradient. A line wider
/// than its room fades out at the right edge instead of being sliced mid-glyph,
/// and once it scrolls, fades in at the left.
/// </summary>
public sealed class EdgeFade
{
    /// <summary>Width of each fade, in DIPs.</summary>
    public const float Width = 18;

    // Room above and below the line so ascenders, descenders and the slide-in aren't clipped.
    private const float VerticalRoom = 14;

    private readonly CompositionVisualSurface _surface;
    private readonly SpriteVisual _sprite;
    private readonly CompositionColorGradientStop _leftEdge, _leftIn, _rightIn, _rightEdge;
    private readonly Compositor _compositor;
    private bool _left, _right;

    public EdgeFade(FrameworkElement frame, UIElement host)
    {
        var source = ElementCompositionPreview.GetElementVisual(host);
        _compositor = source.Compositor;
        _surface = _compositor.CreateVisualSurface();
        _surface.SourceVisual = source;

        var content = _compositor.CreateSurfaceBrush(_surface);
        content.Stretch = CompositionStretch.None;
        content.HorizontalAlignmentRatio = 0;
        content.VerticalAlignmentRatio = 0;

        var fade = _compositor.CreateLinearGradientBrush();
        fade.StartPoint = new Vector2(0, 0);
        fade.EndPoint = new Vector2(1, 0);
        _leftEdge = _compositor.CreateColorGradientStop(0, Colors.White);
        _leftIn = _compositor.CreateColorGradientStop(0, Colors.White);
        _rightIn = _compositor.CreateColorGradientStop(1, Colors.White);
        _rightEdge = _compositor.CreateColorGradientStop(1, Colors.White);
        foreach (var s in new[] { _leftEdge, _leftIn, _rightIn, _rightEdge }) fade.ColorStops.Add(s);

        var mask = _compositor.CreateMaskBrush();
        mask.Source = content;
        mask.Mask = fade;

        _sprite = _compositor.CreateSpriteVisual();
        _sprite.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(frame, _sprite);
        // The surface still captures a source whose own opacity is 0; hiding it leaves only the masked copy.
        source.Opacity = 0;
    }

    public void Resize(Size host)
    {
        if (host.Width <= 0 || host.Height <= 0) return;
        var size = new Vector2((float)host.Width, (float)host.Height + 2 * VerticalRoom);
        _surface.SourceOffset = new Vector2(0, -VerticalRoom);
        _surface.SourceSize = size;
        _sprite.Size = size;
        _sprite.Offset = new Vector3(0, -VerticalRoom, 0);
        var f = Math.Min(0.45f, Width / (float)host.Width);
        _leftIn.Offset = f;
        _rightIn.Offset = 1 - f;
    }

    /// <summary>
    /// A new line starts: the left edge's fade is bound to how far <paramref name="line"/> has
    /// scrolled (none at its leftmost position, full once it has moved one fade width), so
    /// the line's first letters are never faded, whatever the scroll's easing or timing.
    /// The right edge fades while the line runs past it.
    /// </summary>
    public void Begin(bool scrolls, bool animate, UIElement line)
    {
        _leftEdge.StopAnimation("Color");
        _leftEdge.Color = Colors.White;
        var follow = _compositor.CreateExpressionAnimation("ColorLerp(clear, faded, Clamp(-line.Translation.X / width, 0, 1))");
        follow.SetColorParameter("clear", Colors.White);
        follow.SetColorParameter("faded", Color.FromArgb(0, 255, 255, 255));
        follow.SetScalarParameter("width", Width);
        follow.SetReferenceParameter("line", ElementCompositionPreview.GetElementVisual(line));
        _leftEdge.StartAnimation("Color", follow);
        _left = false;
        if (scrolls != _right) Stop(_rightEdge, scrolls, animate, default);
        _right = scrolls;
    }

    /// <summary>Shows or hides each edge's fade, optionally after a delay (the scroll's start).</summary>
    public void Set(bool left, bool right, bool animate, TimeSpan leftDelay = default)
    {
        if (left != _left) Stop(_leftEdge, left, animate, leftDelay);
        if (right != _right) Stop(_rightEdge, right, animate, default);
        _left = left;
        _right = right;
    }

    private void Stop(CompositionColorGradientStop stop, bool faded, bool animate, TimeSpan delay)
    {
        var to = faded ? Color.FromArgb(0, 255, 255, 255) : Colors.White;
        stop.StopAnimation("Color");
        if (!animate || Motion.Reduced)
        {
            stop.Color = to;
            return;
        }
        var a = _compositor.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(1, to);
        a.Duration = TimeSpan.FromMilliseconds(260);
        if (delay > TimeSpan.Zero)
        {
            a.DelayTime = delay;
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueAfterDelay;
        }
        stop.StartAnimation("Color", a);
    }
}
