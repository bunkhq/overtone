using System.Runtime.InteropServices;
using System.Text.Json;

namespace Overtone.Core;

/// <summary>Colours are 0x00RRGGBB. Mirrors <c>OtPalette</c> in core/src/ffi.rs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OtPalette
{
    public uint Tone; // 0 dark card, 1 light card
    public uint Base, Accent, Ink, InkSoft, InkFaint;
    public uint Mesh0, Mesh1, Mesh2;

    public readonly bool IsLight => Tone == 1;
}

/// <summary>P/Invoke surface of overtone_core.dll (the Rust core).</summary>
internal static unsafe partial class CoreLib
{
    private const string Dll = "overtone_core";

    [DllImport(Dll)] private static extern nint ot_version();
    [DllImport(Dll)] private static extern int ot_palette_from_rgba(byte* rgba, nuint len, uint w, uint h, int forceTone, OtPalette* outPalette);
    [DllImport(Dll)] private static extern uint ot_backdrop_size();
    [DllImport(Dll)] private static extern int ot_backdrop_from_rgba(byte* rgba, nuint len, uint w, uint h, byte* outPx, nuint outLen);
    [DllImport(Dll)] internal static extern nint ot_clock_new(long durationMs);
    [DllImport(Dll)] internal static extern void ot_clock_free(nint clock);
    [DllImport(Dll)] internal static extern int ot_clock_report(nint clock, ulong positionMs, ulong reportedAtMs, byte playing, double rate, ulong nowMs);
    [DllImport(Dll)] internal static extern ulong ot_clock_position(nint clock, ulong nowMs);
    [DllImport(Dll)] internal static extern long ot_clock_wall_ms_until(nint clock, ulong targetMs, ulong nowMs);
    [DllImport(Dll)] private static extern nint ot_lyrics_lookup(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string artist,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string album,
        long durationMs);
    [DllImport(Dll)] private static extern void ot_string_free(nint s);
    [DllImport(Dll)] private static extern int ot_is_catalog_song(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string artist);
    [DllImport(Dll)] private static extern int ot_tray_colors(OtPalette* palette, byte darkTaskbar, uint* outColors);
    [DllImport(Dll)] private static extern nint ot_cover_url(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string artist,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string album,
        long durationMs);
    [DllImport(Dll)] private static extern uint ot_mix_oklch(uint a, uint b, float t);
    [DllImport(Dll)] private static extern uint ot_tint_oklch(uint baseColor, uint toward, float t);
    [DllImport(Dll)] private static extern float ot_flat_tint();
    [DllImport(Dll)] private static extern nint ot_censor([MarshalAs(UnmanagedType.LPUTF8Str)] string text, int level);

    public static string Version => Marshal.PtrToStringUTF8(ot_version()) ?? "?";

    /// <summary>The text with filtered words replaced by asterisks, one per character (core/src/censor.rs).</summary>
    /// <param name="level">0 off, 1 slurs and offensive terms, 2 all swear words.</param>
    public static string Censor(string text, int level)
    {
        if (level == 0 || string.IsNullOrEmpty(text)) return text;
        var ptr = ot_censor(text, level);
        try { return Marshal.PtrToStringUTF8(ptr) ?? text; }
        finally { ot_string_free(ptr); }
    }

    /// <summary>Blocking: does iTunes or Deezer know this song? Null when neither could be reached.</summary>
    public static bool? IsCatalogSong(string title, string artist) => ot_is_catalog_song(title, artist) switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    /// <summary>The tray icon's three wave colours (top to bottom) for a palette, readable on the taskbar.</summary>
    public static uint[] TrayColors(OtPalette palette, bool darkTaskbar)
    {
        var colors = new uint[3];
        fixed (uint* c = colors)
        {
            ot_tray_colors(&palette, darkTaskbar ? (byte)1 : (byte)0, c);
        }
        return colors;
    }

    /// <summary>Blocking iTunes lookup for a public cover URL (Discord needs a link). Null when no confident match.</summary>
    public static string? CoverUrl(string title, string artist, string album, long? durationMs)
    {
        var ptr = ot_cover_url(title, artist, album, durationMs ?? -1);
        try
        {
            var url = Marshal.PtrToStringUTF8(ptr);
            return string.IsNullOrEmpty(url) ? null : url;
        }
        finally
        {
            ot_string_free(ptr);
        }
    }

    /// <summary>OKLCH blend of two 0x00RRGGBB colours, for palette cross-fades.</summary>
    public static uint MixOklch(uint a, uint b, float t) => ot_mix_oklch(a, b, t);

    /// <summary>Base tinted toward the accent with its lightness held, so inks keep their contrast.</summary>
    public static uint FlatTint(uint baseColor, uint accent) => ot_tint_oklch(baseColor, accent, FlatTintAmount);

    private static readonly float FlatTintAmount = ot_flat_tint();

    /// <param name="forceTone">-1 auto, 0 dark, 1 light.</param>
    public static OtPalette Palette(ReadOnlySpan<byte> rgba, int width, int height, int forceTone = -1)
    {
        OtPalette p;
        fixed (byte* px = rgba)
        {
            ot_palette_from_rgba(rgba.IsEmpty ? null : px, (nuint)rgba.Length, (uint)width, (uint)height, forceTone, &p);
        }
        return p;
    }

    public static int BackdropSize { get; } = (int)ot_backdrop_size();

    /// <summary>Blurred, saturated, opaque RGBA backdrop (BackdropSize²), or null on bad input.</summary>
    public static byte[]? Backdrop(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var outPx = new byte[BackdropSize * BackdropSize * 4];
        fixed (byte* src = rgba)
        fixed (byte* dst = outPx)
        {
            return ot_backdrop_from_rgba(src, (nuint)rgba.Length, (uint)width, (uint)height, dst, (nuint)outPx.Length) == 0 ? outPx : null;
        }
    }

    /// <summary>Blocking LRCLIB lookup. Call off the UI thread.</summary>
    public static JsonDocument LookupLyrics(string title, string artist, string album, long? durationMs)
    {
        var ptr = ot_lyrics_lookup(title, artist, album, durationMs ?? -1);
        try
        {
            return JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr) ?? """{"status":"error","error":"null"}""");
        }
        finally
        {
            ot_string_free(ptr);
        }
    }
}

/// <summary>Playback clock owned by the Rust core (interpolation + jitter tolerance).</summary>
public sealed class PlaybackClock : IDisposable
{
    private nint _handle;

    public PlaybackClock(long? durationMs)
    {
        DurationMs = durationMs;
        _handle = CoreLib.ot_clock_new(durationMs ?? -1);
    }

    public long? DurationMs { get; }
    public bool Playing { get; private set; }
    public double Rate { get; private set; } = 1;

    public static ulong Now => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <returns>True when animations should re-anchor (state change or real seek).</returns>
    public bool Report(long positionMs, long reportedAtMs, bool playing, double rate)
    {
        var changed = CoreLib.ot_clock_report(_handle, (ulong)Math.Max(0, positionMs), (ulong)Math.Max(0, reportedAtMs), playing ? (byte)1 : (byte)0, rate, Now) == 1;
        Playing = playing;
        Rate = rate > 0 && double.IsFinite(rate) ? rate : 1;
        return changed;
    }

    public long Position => (long)CoreLib.ot_clock_position(_handle, Now);

    /// <summary>Wall-clock ms until playback reaches <paramref name="targetMs"/>, or null when paused.</summary>
    public long? WallMsUntil(long targetMs)
    {
        var v = CoreLib.ot_clock_wall_ms_until(_handle, (ulong)Math.Max(0, targetMs), Now);
        return v < 0 ? null : v;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CoreLib.ot_clock_free(_handle);
            _handle = 0;
        }
    }
}
