namespace Overtone.Platform;

/// <summary>
/// System-wide shortcuts, the same three as on macOS (there Control-Option-Command,
/// here Ctrl+Alt+Shift): show or hide the overlay, ghost mode, next layout.
/// RegisterHotKey on the overlay's window; a shortcut another app already holds is
/// skipped, and the tray menu still works.
/// </summary>
public sealed class HotKeys : IDisposable
{
    public sealed record Shortcut(int Id, uint Key)
    {
        /// <summary>For the tray menu, after a tab so Windows right-aligns it.</summary>
        public string Label => $"Ctrl+Alt+Shift+{(char)Key}";
    }

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    public static readonly Shortcut ToggleOverlay = new(1, 'O');
    public static readonly Shortcut Ghost = new(2, 'G');
    public static readonly Shortcut CycleLayout = new(3, 'L');

    private readonly nint _hwnd;
    private readonly Dictionary<int, Action> _actions = new();

    public HotKeys(nint hwnd)
    {
        _hwnd = hwnd;
        Win32.HotKeyPressed += OnHotKey;
    }

    public void Register(Shortcut shortcut, Action action)
    {
        if (Win32.RegisterHotKey(_hwnd, shortcut.Id, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, shortcut.Key))
            _actions[shortcut.Id] = action;
    }

    private void OnHotKey(int id)
    {
        if (_actions.TryGetValue(id, out var action)) action();
    }

    public void Dispose()
    {
        Win32.HotKeyPressed -= OnHotKey;
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }
}
