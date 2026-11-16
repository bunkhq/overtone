using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Overtone.Overlay;

/// <summary>
/// Windows 11 Acrylic (the material behind system flyouts), tinted with the track's
/// base colour. The stock DesktopAcrylicBackdrop turns into a flat fallback whenever
/// its window is inactive, and the overlay is never activated, so this one drives
/// the controller itself and reports the window as always active.
/// </summary>
public sealed class NativeAcrylicBackdrop : SystemBackdrop
{
    private readonly SystemBackdropConfiguration _config = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;
    private Color _tint = Color.FromArgb(255, 32, 32, 32);
    private bool _light;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_config);
        Apply();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>Tints the material with the palette's base colour (0x00RRGGBB) for a dark or light card.</summary>
    public void SetTint(uint baseColor, bool light)
    {
        _tint = Color.FromArgb(255, (byte)(baseColor >> 16), (byte)(baseColor >> 8), (byte)baseColor);
        _light = light;
        Apply();
    }

    private void Apply()
    {
        _config.Theme = _light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        if (_controller is null) return;
        // Strong enough that the palette's inks keep their contrast over a busy desktop,
        // light enough that the blur still reads as Acrylic.
        _controller.TintColor = _tint;
        _controller.TintOpacity = _light ? 0.7f : 0.62f;
        _controller.LuminosityOpacity = _light ? 0.9f : 0.88f;
        _controller.FallbackColor = _tint;
    }
}
