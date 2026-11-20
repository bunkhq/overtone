using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Overtone.Platform;

/// <summary>
/// How bright the screen is around a window: the mean relative luminance (0 black,
/// 1 white) of a thin band just outside it. The window's own pixels are never read,
/// so its text can't skew the result, and nothing has to hide it from capture (which
/// would also hide it from screenshots and screen sharing). One GDI copy of the
/// composed screen, every other pixel. The copy blocks for about 10 ms while the
/// compositor hands the frame over (little of it CPU), so call it off the UI thread.
/// </summary>
internal static unsafe class ScreenSample
{
    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    /// <summary>sRGB to linear light, per channel value.</summary>
    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        var c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    /// <summary>
    /// The mean luminance of a <paramref name="band"/>-pixel ring around
    /// <paramref name="window"/>, clipped to <paramref name="screen"/> (its monitor);
    /// null if none of the ring is on screen or the copy fails.
    /// </summary>
    public static double? Around(RectInt32 window, RectInt32 screen, int band)
    {
        // One copy of the window plus the ring: each screen copy waits on the compositor
        // (about 10 ms whatever its size), so four strips would cost four times as much.
        var outer = new RectInt32(window.X - band, window.Y - band, window.Width + 2 * band, window.Height + 2 * band);
        if (Clip(outer, screen) is not { } r) return null;
        var screenDc = GetDC(0);
        if (screenDc == 0) return null;
        try
        {
            return Ring(screenDc, r, window);
        }
        finally
        {
            ReleaseDC(0, screenDc);
        }
    }

    private static RectInt32? Clip(RectInt32 a, RectInt32 b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > x && btm > y ? new RectInt32(x, y, r - x, btm - y) : null;
    }

    /// <summary>Copies <paramref name="r"/> and averages the pixels outside <paramref name="hole"/>.</summary>
    private static double? Ring(nint screenDc, RectInt32 r, RectInt32 hole)
    {
        var info = new BITMAPINFOHEADER
        {
            Size = sizeof(BITMAPINFOHEADER),
            Width = r.Width,
            Height = -r.Height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var dc = CreateCompatibleDC(screenDc);
        if (dc == 0) return null;
        var bmp = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
        if (bmp == 0)
        {
            DeleteDC(dc);
            return null;
        }
        var old = SelectObject(dc, bmp);
        try
        {
            if (!BitBlt(dc, 0, 0, r.Width, r.Height, screenDc, r.X, r.Y, SRCCOPY)) return null;
            // The hole in the copy's coordinates: the window's own pixels are skipped.
            int hx0 = hole.X - r.X, hy0 = hole.Y - r.Y, hx1 = hx0 + hole.Width, hy1 = hy0 + hole.Height;
            double sum = 0;
            long n = 0;
            var px = (byte*)bits;
            for (var y = 0; y < r.Height; y += 2)
            {
                var row = px + (long)y * r.Width * 4;
                var inBand = y < hy0 || y >= hy1;
                for (var x = 0; x < r.Width; x += 2)
                {
                    if (!inBand && x >= hx0 && x < hx1)
                    {
                        x = hx1 - 2; // jump the hole: the step lands on its far side
                        continue;
                    }
                    var p = row + x * 4; // BGRA
                    sum += 0.2126 * Linear[p[2]] + 0.7152 * Linear[p[1]] + 0.0722 * Linear[p[0]];
                    n++;
                }
            }
            return n == 0 ? null : sum / n;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
        }
    }
}
