using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Overtone.Overlay;

/// <summary>A spring in SwiftUI terms: response (s) and damping fraction.</summary>
public readonly record struct Spring(double Response, double Damping)
{
    /// <summary>Analytic step response from rest: 0 at t=0, settling at 1.</summary>
    public double At(double t)
    {
        var w = 2 * Math.PI / Response;
        var z = Damping;
        if (z >= 1) return 1 - Math.Exp(-w * t) * (1 + w * t);
        var wd = w * Math.Sqrt(1 - z * z);
        return 1 - Math.Exp(-z * w * t) * (Math.Cos(wd * t) + z * w / wd * Math.Sin(wd * t));
    }

    /// <summary>Time until the remaining motion is under 0.1%.</summary>
    public double SettleSeconds => 6.9 / (Damping * 2 * Math.PI / Response);
}

/// <summary>
/// Motion tokens (docs/ARCHITECTURE.md §6) and composition helpers. Animations run
/// off the UI thread, but WinUI 3's compositor lives in this process: every running
/// animation renders frames here. Keep them short; never loop one while idle.
/// </summary>
public static class Motion
{
    public static readonly Spring Float = new(0.55, 0.78);
    public static readonly Spring Morph = new(0.42, 0.86);

    /// <summary>Card size between modes: quick, nearly critically damped, so it reads as a resize, not a bounce.</summary>
    public static readonly Spring Resize = new(0.34, 0.9);
    /// <summary>
    /// Lyric moves happen every few seconds, so they use a short eased glide rather
    /// than a spring: a spring at 0.5 s / 0.92 renders well over a second of frames.
    /// </summary>
    public static readonly TimeSpan Lyric = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan PaletteFade = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan ReducedFade = TimeSpan.FromMilliseconds(150);

    private static readonly UISettings Ui = new();

    /// <summary>OS "Animation effects" switch. Off means fades only, no movement.</summary>
    public static bool Reduced => !Ui.AnimationsEnabled;

    /// <summary>OS "Transparency effects" switch.</summary>
    public static bool Opaque => !Ui.AdvancedEffectsEnabled;

    /// <summary>Calls <paramref name="changed"/> (on a background thread) when the transparency switch flips.</summary>
    public static void WatchOpaque(Action changed) => Ui.AdvancedEffectsEnabledChanged += (_, _) => changed();

    public static Visual V(UIElement e) => ElementCompositionPreview.GetElementVisual(e);

    public static void EnableTranslation(UIElement e) => ElementCompositionPreview.SetIsTranslationEnabled(e, true);

    public static void Fade(UIElement e, float to, double ms, float? from = null, double delayMs = 0)
    {
        var v = V(e);
        v.StopAnimation("Opacity");
        if (ms <= 0)
        {
            v.Opacity = to;
            return;
        }
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        if (from is { } f) a.InsertKeyFrame(0, f);
        a.InsertKeyFrame(1, to, v.Compositor.CreateCubicBezierEasingFunction(new(0.2f, 0.8f), new(0.3f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        if (delayMs > 0)
        {
            a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        v.StartAnimation("Opacity", a);
    }

    /// <summary>Springs a Vector3 property ("Scale" or "Translation"). Under reduced motion it jumps.</summary>
    public static void SpringTo(UIElement e, string property, Vector3 to, Spring s, Vector3? from = null)
    {
        var v = V(e);
        v.StopAnimation(property);
        if (Reduced)
        {
            Set(v, property, to);
            return;
        }
        if (from is { } f) Set(v, property, f);
        var a = v.Compositor.CreateSpringVector3Animation();
        a.Period = TimeSpan.FromSeconds(s.Response);
        a.DampingRatio = (float)s.Damping;
        a.FinalValue = to;
        a.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        v.StartAnimation(property, a);
    }

    /// <summary>Short ease-out glide for frequent, small moves. Under reduced motion it jumps.</summary>
    public static void GlideTo(UIElement e, string property, Vector3 to, TimeSpan duration, Vector3? from = null)
    {
        var v = V(e);
        v.StopAnimation(property);
        if (Reduced)
        {
            Set(v, property, to);
            return;
        }
        var a = v.Compositor.CreateVector3KeyFrameAnimation();
        if (from is { } f) a.InsertKeyFrame(0, f);
        // Ease-out quint: quick departure, long soft landing.
        a.InsertKeyFrame(1, to, v.Compositor.CreateCubicBezierEasingFunction(new(0.22f, 1f), new(0.36f, 1f)));
        a.Duration = duration;
        v.StartAnimation(property, a);
    }

    /// <summary>
    /// Animates one component of a Vector3 property (e.g. "Translation.X"), so two
    /// motions on different axes can run independently.
    /// </summary>
    public static void Slide(UIElement e, string channel, float to, TimeSpan duration, float? from = null,
        TimeSpan delay = default, bool scroll = false)
    {
        var v = V(e);
        v.StopAnimation(channel);
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        if (from is { } f) a.InsertKeyFrame(0, f);
        a.InsertKeyFrame(1, to, scroll
            ? c.CreateCubicBezierEasingFunction(new(0.45f, 0f), new(0.55f, 1f)) // ease-in-out for scrolling text
            : c.CreateCubicBezierEasingFunction(new(0.22f, 1f), new(0.36f, 1f)));
        a.Duration = duration;
        if (delay > TimeSpan.Zero)
        {
            a.DelayTime = delay;
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        v.StartAnimation(channel, a);
    }

    /// <summary>Sets one component of a Vector3 property immediately.</summary>
    /// <remarks>
    /// Writes the whole Translation vector with the other axis's static value: if that
    /// axis is mid-animation its in-flight value is lost. Use <see cref="Set"/> to reset both.
    /// </remarks>
    public static void SetChannel(UIElement e, string channel, float value)
    {
        var v = V(e);
        v.StopAnimation(channel);
        v.Properties.TryGetVector3("Translation", out var t);
        v.Properties.InsertVector3("Translation", channel.EndsWith(".X") ? t with { X = value } : t with { Y = value });
    }

    public static void Set(Visual v, string property, Vector3 value)
    {
        v.StopAnimation(property);
        if (property == "Scale") v.Scale = value;
        else v.Properties.InsertVector3(property, value);
    }
}
