using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Overtone.Overlay;

public sealed partial class TransportBar : UserControl
{
    public TransportBar() => InitializeComponent();

    public event Action? Previous, PlayPause, Next;

    /// <summary>The buttons on show (compact hides previous and next).</summary>
    public IEnumerable<FrameworkElement> Buttons =>
        new FrameworkElement[] { PrevButton, PlayButton, NextButton }.Where(b => b.Visibility == Visibility.Visible);

    private bool _native, _playing = true;
    private double _button = 32, _glyph = 16, _radius = 10;

    /// <param name="button">Hit target in DIPs.</param>
    /// <param name="glyph">Glyph size for previous/next; play/pause gets +2.</param>
    public void Size(double button, double glyph, double radius)
    {
        (_button, _glyph, _radius) = (button, glyph, radius);
        Apply();
    }

    /// <summary>Native look: Segoe Fluent Icons glyphs and Windows 11's 4 px control corners.</summary>
    public void SetNative(bool native)
    {
        _native = native;
        Apply();
    }

    /// <summary>
    /// Play/pause alone, for a narrow card: previous and next give their width to the text.
    /// Media keys and the player still skip.
    /// </summary>
    public void SetCompact(bool compact)
    {
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        if (PrevButton.Visibility != v) PrevButton.Visibility = NextButton.Visibility = v;
    }

    private void Apply()
    {
        foreach (var b in new[] { PrevButton, PlayButton, NextButton })
        {
            b.Width = b.Height = _button;
            b.CornerRadius = new CornerRadius(_native ? 4 : _radius);
        }
        PrevBox.Width = PrevBox.Height = NextBox.Width = NextBox.Height = _glyph;
        PlayBox.Width = PlayBox.Height = _glyph + 2;
        // Fluent glyphs carry more internal padding than the drawn ones: a touch smaller reads the same.
        PrevIcon.FontSize = NextIcon.FontSize = PlayIcon.FontSize = Math.Round(_glyph * 0.9);
        var drawn = _native ? Visibility.Collapsed : Visibility.Visible;
        var icon = _native ? Visibility.Visible : Visibility.Collapsed;
        PrevBox.Visibility = NextBox.Visibility = PlayBox.Visibility = drawn;
        PrevIcon.Visibility = NextIcon.Visibility = PlayIcon.Visibility = icon;
        SetPlaying(_playing);
    }

    public void SetPlaying(bool playing)
    {
        _playing = playing;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PlayIcon.Glyph = playing ? "\uF8AE" : "\uF5B0"; // PauseSolid / PlaySolid (Segoe Fluent Icons)
        AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
    }

    private void OnPrev(object sender, RoutedEventArgs e) => Previous?.Invoke();
    private void OnPlay(object sender, RoutedEventArgs e) => PlayPause?.Invoke();
    private void OnNext(object sender, RoutedEventArgs e) => Next?.Invoke();
}
