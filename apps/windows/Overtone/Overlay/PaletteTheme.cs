using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Overtone.Core;
using Windows.UI;

namespace Overtone.Overlay;

/// <summary>
/// The live palette. Owns the shared brushes in App.xaml and cross-fades them in
/// OKLCH (through the Rust core) when the track changes. The per-frame callback
/// only runs for the 600 ms of a fade, then detaches.
/// </summary>
public sealed class PaletteTheme
{
    private const int Roles = 6; // base, accent, ink, inkSoft, inkFaint, title

    private readonly SolidColorBrush _base = Brush("BaseBrush"), _scrim = Brush("ScrimBrush"), _accent = Brush("AccentBrush"),
        _ink = Brush("InkBrush"), _inkSoft = Brush("InkSoftBrush"), _inkFaint = Brush("InkFaintBrush"), _title = Brush("TitleBrush"),
        _track = Brush("TrackBrush"), _hover = Brush("HoverBrush"), _pressed = Brush("PressedBrush"),
        _rim = Brush("RimBrush"), _placeholder = Brush("PlaceholderBrush");

    // Flat mode: the base colour, warmed toward the accent in the top-left corner.
    private readonly LinearGradientBrush _tint = (LinearGradientBrush)Application.Current.Resources["TintBrush"];

    private readonly uint[] _now = new uint[Roles];
    private uint[] _from = new uint[Roles], _to = new uint[Roles];
    private DateTime _start;
    private bool _running;

    public PaletteTheme()
    {
        var neutral = CoreLib.Palette([], 0, 0);
        Apply(neutral, animate: false);
    }

    public OtPalette Current { get; private set; }

    public void Apply(OtPalette p, bool animate)
    {
        Current = p;
        // The highlight (title, current lyric, controls) is the accent; the core solves
        // it to 4.5:1 on the base. Fall back to ink if a palette ever arrives without that.
        var title = Contrast(p.Accent, p.Base) >= 4.5 ? p.Accent : p.Ink;
        _to = [p.Base, p.Accent, p.Ink, p.InkSoft, p.InkFaint, title];
        if (!animate)
        {
            Stop();
            Array.Copy(_to, _now, Roles);
            Write(_now);
            return;
        }
        _from = (uint[])_now.Clone();
        _start = DateTime.UtcNow;
        if (!_running)
        {
            _running = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        var t = (float)Math.Clamp((DateTime.UtcNow - _start) / Motion.PaletteFade, 0, 1);
        var eased = 1 - MathF.Pow(1 - t, 3); // ease-out cubic
        for (var i = 0; i < Roles; i++) _now[i] = CoreLib.MixOklch(_from[i], _to[i], eased);
        Write(_now);
        if (t >= 1) Stop();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
    }

    private void Write(uint[] c)
    {
        var ink = c[2];
        _base.Color = Rgb(c[0]);
        _scrim.Color = Rgb(c[0], 204);
        // Hue and chroma move toward the accent, lightness stays: inks keep their
        // contrast on the tinted corner (core test flat_tint_corner_keeps_ink_contrast).
        _tint.GradientStops[0].Color = Rgb(CoreLib.FlatTint(c[0], c[1]));
        _tint.GradientStops[1].Color = Rgb(c[0]);
        _accent.Color = Rgb(c[1]);
        _ink.Color = Rgb(ink);
        _inkSoft.Color = Rgb(c[3]);
        _inkFaint.Color = Rgb(c[4]);
        _title.Color = Rgb(c[5]);
        // Unfilled progress: a grey wash on a dark card, a lighter band on a light one
        // (as on a pale sleeve, where the empty track reads paler than the ground).
        _track.Color = Current.IsLight ? Rgb(0xFFFFFF, 140) : Rgb(ink, 61); // 55 % white / 24 % ink
        _hover.Color = Rgb(ink, 31);   // 12 %
        _pressed.Color = Rgb(ink, 51); // 20 %
        _rim.Color = Rgb(ink, 20);     // 8 %
        _placeholder.Color = Rgb(ink, 26);
    }

    /// <summary>WCAG contrast ratio of two 0xRRGGBB colours.</summary>
    public static double Contrast(uint a, uint b)
    {
        static double Lin(uint v) { var x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double Lum(uint c) => 0.2126 * Lin(c >> 16 & 0xFF) + 0.7152 * Lin(c >> 8 & 0xFF) + 0.0722 * Lin(c & 0xFF);
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Rgb(uint c, byte a = 255) => Color.FromArgb(a, (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
}
