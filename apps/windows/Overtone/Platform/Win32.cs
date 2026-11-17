using System.Runtime.InteropServices;

namespace Overtone.Platform;

internal static class Win32
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x2;
    private const uint WM_HOTKEY = 0x0312;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const nint MA_NOACTIVATE = 3;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref uint value, int size);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    /// <summary>Raised on the UI thread with the id of a global hotkey registered on the overlay window.</summary>
    public static event Action<int>? HotKeyPressed;

    // Held for the life of the process so the GC never collects the callback.
    private static readonly SubclassProc Proc = OverlayProc;

    /// <summary>
    /// Floating, never-focused tool window: no taskbar button, no Alt+Tab entry,
    /// no frame, and clicks don't take focus from the app you're working in.
    /// </summary>
    public static void MakeOverlay(nint hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);

        // The presenter keeps a dialog frame even with border and title bar off, and
        // re-applies it on show. Rather than fight the styles, report a zero-width
        // non-client area, so the client (the transparent XAML surface) is the whole window.
        SetWindowSubclass(hwnd, Proc, 1, 0);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // The card draws its own rounded corners; DWM must not round or outline the window.
        int corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        uint none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
    }

    /// <summary>
    /// Native look: Windows draws the frame (its shadow, 1 px border and standard 8 px
    /// rounded corners), as for system flyouts. Off: no rounding, no border, and the
    /// card paints its own corners and shadow. The client area stays the whole window
    /// either way (zero-width non-client area); the thick-frame style only tells DWM
    /// this is a framed window that gets a shadow.
    /// </summary>
    public static void SetNativeFrame(nint hwnd, bool native)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        style = native ? style | WS_THICKFRAME : style & ~WS_THICKFRAME;
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);
        int corner = native ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        uint border = native ? DWMWA_COLOR_DEFAULT : DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Ghost mode: clicks pass through the window to whatever is under it. Only a
    /// layered window is skipped by hit-testing across processes, so it turns layered
    /// (fully opaque: the dimming is the XAML content's) while click-through.
    /// </summary>
    public static void SetClickThrough(nint hwnd, bool on)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_LAYERED | WS_EX_TRANSPARENT : ex & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
        if (on) SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static unsafe nint OverlayProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != 0:
                return 0;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_HOTKEY:
                HotKeyPressed?.Invoke((int)wParam);
                return 0;
            case WM_WINDOWPOSCHANGING:
                // The presenter re-applies its own z-order asynchronously after a show,
                // which can silently drop us out of the topmost band. Own z-order here:
                // any z-order change lands in the topmost band.
                var pos = (WINDOWPOS*)lParam;
                if ((pos->flags & SWP_NOZORDER) == 0) pos->hwndInsertAfter = HWND_TOPMOST;
                break;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    /// <summary>
    /// Puts the window in the topmost band without activating it. The presenter's
    /// IsAlwaysOnTop does not stick on a window that was hidden when it was set.
    /// </summary>
    public static void SetTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    private const uint GA_ROOT = 2;

    /// <summary>Whether the cursor is over <paramref name="hwnd"/> (or one of its child windows).</summary>
    public static bool CursorOverWindow(nint hwnd) =>
        GetCursorPos(out var p) && GetAncestor(WindowFromPoint(p), GA_ROOT) == hwnd;

    public static double Scale(nint hwnd) => Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;
}
