using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Overtone.Core;

namespace Overtone.Platform;

/// <summary>
/// Draws the tray icon: the Overtone mark (three standing waves, the same geometry
/// as scripts/gen-icon.mjs) in colours from the current track's palette, sized
/// for the tray at the system DPI. Redrawn once per track, never animated.
/// </summary>
public sealed class TrayIconArt : IDisposable
{
    // Mark geometry on the 1024 grid, cropped to the tray viewBox (132..892).
    private static readonly (int Mode, float Yc, float Amp)[] Waves = [(1, 326, 80), (2, 526, 64), (3, 726, 52)];
    private const float ViewMin = 132, ViewSize = 760, X0 = 176, X1 = 848, Stroke = 92;

    private nint _hicon;

    /// <summary>Whether the taskbar uses the dark theme (Settings, Personalisation, Colours, "Choose your mode").</summary>
    public static bool DarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The icon for <paramref name="palette"/>. The previous icon's handle is released.</summary>
    public Icon Render(OtPalette palette)
    {
        var colors = CoreLib.TrayColors(palette, DarkTaskbar());
        var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
        if (size <= 0) size = 16;

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            var f = size / ViewSize;
            for (var i = 0; i < Waves.Length; i++)
            {
                var (mode, yc, amp) = Waves[i];
                var pts = new PointF[97];
                for (var k = 0; k < pts.Length; k++)
                {
                    var t = k / (float)(pts.Length - 1);
                    var x = X0 + (X1 - X0) * t;
                    var y = yc - amp * MathF.Sin(mode * MathF.PI * t);
                    pts[k] = new PointF((x - ViewMin) * f, (y - ViewMin) * f);
                }
                var c = colors[i];
                using var pen = new Pen(Color.FromArgb(255, (int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF)), Stroke * f)
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
                };
                g.DrawLines(pen, pts);
            }
        }

        var old = _hicon;
        _hicon = bmp.GetHicon();
        if (old != 0) DestroyIcon(old);
        return Icon.FromHandle(_hicon);
    }

    public void Dispose()
    {
        if (_hicon != 0) DestroyIcon(_hicon);
        _hicon = 0;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
