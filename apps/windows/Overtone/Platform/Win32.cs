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

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(nint process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    /// <summary>
    /// The overlay never has focus, so Windows 11 counts it as background work and may put
    /// it on EcoQoS (efficiency cores, low clocks) and coarsen its timers: lyric glides and
    /// line changes then hitch while the rest of the machine is fine. Opting out of both
    /// throttles (execution speed, timer resolution) keeps them on time; the app is idle
    /// between frames anyway, so the power cost is small.
    /// </summary>
    public static void OptOutOfThrottling()
    {
        const int ProcessPowerThrottling = 4;
        const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

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

    /// <summary>
    /// Ghost mode with live controls: while the pointer is over a control the window
    /// takes clicks again. Only the transparent bit flips (the window stays layered),
    /// and hit-testing reads it live, so no frame change is needed.
    /// </summary>
    public static void SetHitThrough(nint hwnd, bool through)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var want = through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)want);
    }

    /// <summary>
    /// Whole-window opacity (0-255), for fading the native-look window, whose frame,
    /// shadow and Acrylic are drawn by Windows and so can't fade with the content.
    /// Makes the window layered; <see cref="EndAlpha"/> undoes that.
    /// </summary>
    public static void SetAlpha(nint hwnd, byte alpha)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex | WS_EX_LAYERED));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    /// <summary>Fully opaque again; the window stays layered while click-through (ghost mode) needs it.</summary>
    public static void EndAlpha(nint hwnd, bool keepLayered)
    {
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        if (keepLayered) return;
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) != 0) SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(ex & ~WS_EX_LAYERED));
    }

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public static nint GetForeground() => GetForegroundWindow();

    /// <summary>True when the foreground window belongs to this process (a popup of ours, say).</summary>
    public static bool ForegroundIsOurs()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);

    /// <summary>
    /// Gives Win32 keyboard focus to the window's XAML content (its child "site bridge"
    /// window). A WinUI window activated from code is foreground but its content may
    /// not hold focus, so keys go nowhere until the first click.
    /// </summary>
    public static void FocusContent(nint hwnd)
    {
        var site = FindWindowExW(hwnd, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        SetFocus(site != 0 ? site : hwnd);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// The notification area's end of the primary taskbar (bottom-right for a bottom
    /// taskbar), where a tray flyout opens from; null if there is no taskbar.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    [DllImport("user32.dll")] private static extern bool CalculatePopupWindowPosition(ref POINT anchor, ref SIZE size, uint flags, ref RECT exclude, out RECT pos);

    public const uint TPM_CENTERALIGN = 0x0004, TPM_RIGHTALIGN = 0x0008, TPM_BOTTOMALIGN = 0x0020,
        TPM_VERTICAL = 0x0040, TPM_WORKAREA = 0x10000;

    /// <summary>The tray icon's rectangle on screen (in the taskbar or the overflow box), or null.</summary>
    public static RECT? IconRect(Guid id, nint hwnd)
    {
        var nid = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, guidItem = id };
        return Shell_NotifyIconGetRect(ref nid, out var r) == 0 && r.Right > r.Left ? r : null;
    }

    /// <summary>
    /// Where a context menu of <paramref name="width"/> × <paramref name="height"/> goes
    /// when opened at <paramref name="anchor"/>: Windows' own menu placement, which flips
    /// to fit the work area and keeps clear of <paramref name="exclude"/>.
    /// </summary>
    public static RECT PopupPosition(POINT anchor, int width, int height, uint flags, RECT exclude)
    {
        var size = new SIZE { cx = width, cy = height };
        if (CalculatePopupWindowPosition(ref anchor, ref size, flags | TPM_WORKAREA, ref exclude, out var pos)) return pos;
        return new RECT { Left = anchor.X, Top = anchor.Y - height, Right = anchor.X + width, Bottom = anchor.Y };
    }

    public static POINT? TrayCorner()
    {
        var bar = FindWindowW("Shell_TrayWnd", null);
        if (bar == 0 || !GetWindowRect(bar, out var r)) return null;
        return new POINT { X = r.Right - 1, Y = r.Bottom - 1 };
    }

    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private static readonly SubclassProc PanelProcRef = PanelProc;

    /// <summary>
    /// The tray panel: a tool window (no taskbar button) that does take focus, so the
    /// keyboard reaches it and clicking away closes it. Windows draws its frame (shadow,
    /// 1 px border, rounded corners) with a zero-width non-client area, as in the
    /// overlay's native look, and every point of it is client, so it never shows
    /// resize cursors.
    /// </summary>
    public static void MakePanel(nint hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
        SetWindowSubclass(hwnd, PanelProcRef, 2, 0);
        SetNativeFrame(hwnd, true);
    }

    private static nint PanelProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data) => msg switch
    {
        WM_NCCALCSIZE when wParam != 0 => 0,
        WM_NCHITTEST => HTCLIENT,
        _ => DefSubclassProc(hwnd, msg, wParam, lParam),
    };

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
