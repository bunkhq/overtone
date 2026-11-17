using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overtone.Platform;

public enum OverlayMode { Pill, Card, Lyrics }

/// <summary>What fills the card behind the text.</summary>
public enum OverlayBackground
{
    /// <summary>The cover, blurred, under a palette scrim.</summary>
    Cover,
    /// <summary>The base colour warming toward the accent in the top-left corner.</summary>
    Gradient,
    /// <summary>The base colour alone.</summary>
    Solid,
}

/// <summary>Dark card with light text, or a pale card with dark text. Auto picks light only for pale covers.</summary>
public enum OverlayTone { Auto, Dark, Light, System }

/// <summary>Word filter for what Overtone sends to Discord. Values match the core's levels.</summary>
public enum DiscordFilter { Off = 0, Offensive = 1, All = 2 }

/// <summary>What the Discord member list shows after "Listening to".</summary>
public enum DiscordStatusLine
{
    Artist,
    Title,
    /// <summary>The lyric being sung (the artist between lines and without lyrics).</summary>
    Lyric,
}

/// <summary>Small persisted state in %LOCALAPPDATA%\Overtone\settings.json.</summary>
public sealed class Settings
{
    /// <summary>%LOCALAPPDATA%\Overtone: settings, the error log and caches.</summary>
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overtone");
    private static readonly string Dir = DataDir;
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public OverlayMode Mode { get; set; } = OverlayMode.Pill;

    public OverlayBackground Background { get; set; } = OverlayBackground.Cover;

    public OverlayTone Tone { get; set; } = OverlayTone.Auto;

    /// <summary>Discord Rich Presence on/off (tray menu). Off until the user turns it on.</summary>
    public bool DiscordPresence { get; set; } = false;

    /// <summary>The Discord status line: "Listening to" the artist, the song title or the current lyric (tray menu).</summary>
    public DiscordStatusLine DiscordStatus { get; set; } = DiscordStatusLine.Artist;

    /// <summary>Word filter for the Discord status and card (tray menu). On by default: all swear words.</summary>
    public DiscordFilter DiscordFilter { get; set; } = DiscordFilter.All;

    /// <summary>
    /// Native Windows look (tray menu, on by default): system frame, shadow and corners,
    /// Acrylic tinted with the album colour, Fluent icons. Off: Overtone's own drawn card.
    /// </summary>
    public bool NativeLook { get; set; } = true;

    /// <summary>Tray icon in the track's colours (tray menu, on by default). Off: the brand icon always.</summary>
    public bool ColorTrayIcon { get; set; } = true;

    /// <summary>Show only music: videos and other media in browsers and players are ignored (tray menu).</summary>
    public bool MusicOnly { get; set; } = true;

    /// <summary>
    /// Overrides the Discord application the presence card belongs to (development
    /// only; unset uses Overtone's own, DiscordPresence.DefaultAppId). See docs/DISCORD.md.
    /// </summary>
    public string? DiscordAppId { get; set; }

    /// <summary>The card size the user chose, in DIPs; null until first resized (then the preset for Mode).</summary>
    public double? Width { get; set; }
    public double? Height { get; set; }

    /// <summary>Top-left of the overlay's visible card, in physical screen pixels. Null until first placed.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    /// <summary>False when the user hid the overlay from the tray.</summary>
    public bool Visible { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // "Flat" was the gradient background before Solid existed.
                var text = File.ReadAllText(FilePath).Replace("\"Flat\"", "\"Gradient\"");
                return JsonSerializer.Deserialize<Settings>(text, Json) ?? new Settings();
            }
        }
        catch (Exception)
        {
            // A corrupt file falls back to defaults rather than blocking startup.
        }
        return new Settings();
    }

    /// <summary>Appends to %LOCALAPPDATA%\Overtone\errors.log; the overlay has nowhere to show errors.</summary>
    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "errors.log"), $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch (Exception)
        {
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
            // Losing a remembered position is not worth interrupting the user over.
        }
    }
}
