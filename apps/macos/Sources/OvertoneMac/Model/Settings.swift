import AppKit
import SwiftUI
import Observation
import OvertoneCore

public enum OverlayLayout: String, CaseIterable, Sendable {
    case pill, card, lyrics

    var title: String {
        switch self {
        case .pill: "Pill"
        case .card: "Card"
        case .lyrics: "Lyrics"
        }
    }

    var next: OverlayLayout {
        let all = Self.allCases
        return all[(all.firstIndex(of: self)! + 1) % all.count]
    }

    /// The overlay's height picks its layout, as on Windows: a pill below
    /// `cardFrom`, the player card below `lyricsFrom`, then the lyric panel.
    static let cardFrom: CGFloat = 124
    static let lyricsFrom: CGFloat = 200

    static func forHeight(_ height: CGFloat) -> OverlayLayout {
        height < cardFrom ? .pill : height < lyricsFrom ? .card : .lyrics
    }
}

/// What the Discord member list shows after "Listening to".
public enum DiscordStatusLine: String, CaseIterable, Sendable {
    case artist, title, lyric

    var title: String {
        switch self {
        case .artist: "Artist"
        case .title: "Song Title"
        case .lyric: "Current Lyric"
        }
    }
}

public enum TonePreference: String, CaseIterable, Sendable {
    case auto, system, dark, light

    var title: String {
        switch self {
        case .auto: "Match Artwork"
        case .system: "Match System"
        case .dark: "Always Dark"
        case .light: "Always Light"
        }
    }

    /// The tone to force, given whether the system appearance is dark. `nil`: pick from the artwork.
    func forced(systemDark: Bool) -> Tone? {
        switch self {
        case .auto: nil
        case .system: systemDark ? .dark : .light
        case .dark: .dark
        case .light: .light
        }
    }
}

/// What fills the overlay behind the text (as the Windows Background menu, plus Liquid Glass).
public enum OverlayBackground: String, CaseIterable, Sendable {
    /// The cover, blurred, under a palette scrim.
    case cover
    /// The base colour warming toward the accent in the top-left corner.
    case gradient
    /// The base colour alone.
    case solid
    /// macOS 26 Liquid Glass, tinted with the palette's base colour.
    case glass

    var title: String {
        switch self {
        case .cover: "Blurred Cover"
        case .gradient: "Gradient"
        case .solid: "Solid Colour"
        case .glass: "Liquid Glass"
        }
    }

    static var available: [OverlayBackground] {
        if #available(macOS 26, *) { return allCases }
        return allCases.filter { $0 != .glass }
    }
}

extension CensorLevel {
    var title: String {
        switch self {
        case .off: "Off"
        case .slurs: "Slurs and Offensive Terms"
        case .all: "All Swear Words"
        }
    }

    var key: String {
        switch self {
        case .off: "off"
        case .slurs: "slurs"
        case .all: "all"
        }
    }

    static let ordered: [CensorLevel] = [.all, .slurs, .off]
}

/// User preferences, persisted in UserDefaults. Ghost mode is deliberately not
/// persisted: launching into a click-through overlay would look broken.
@MainActor
@Observable
public final class Settings {
    private let defaults: UserDefaults

    /// The overlay's size, sized freely by dragging its edges (within
    /// `OverlayLayout.minSize...maxSize`). Its height picks the layout.
    public private(set) var overlaySize: CGSize

    func setOverlaySize(_ size: CGSize) {
        let size = OverlayLayout.clamp(size)
        guard size != overlaySize else { return }
        overlaySize = size
        defaults.set(NSStringFromSize(size), forKey: "overlaySize")
    }

    public var layout: OverlayLayout { OverlayLayout.forHeight(overlaySize.height) }

    /// Jumps to a layout's preset size (menu, hotkey, double-click). From there the user resizes freely.
    func applyPreset(_ layout: OverlayLayout) { setOverlaySize(layout.preset) }

    func cycleLayout() { applyPreset(layout.next) }

    public var overlayVisible: Bool {
        didSet { defaults.set(overlayVisible, forKey: "overlayVisible") }
    }

    public var tone: TonePreference {
        didSet { defaults.set(tone.rawValue, forKey: "tone") }
    }

    /// Show only music: videos and other media in browsers and players are ignored (menu, on by default).
    public var musicOnly: Bool {
        didSet { defaults.set(musicOnly, forKey: "musicOnly") }
    }

    /// Menu-bar icon in the track's colours instead of the monochrome template (menu, off by default).
    public var colorMenuBarIcon: Bool {
        didSet { defaults.set(colorMenuBarIcon, forKey: "colorMenuBarIcon") }
    }

    public var background: OverlayBackground {
        didSet { defaults.set(background.rawValue, forKey: "background") }
    }

    /// Word filter for what Overtone sends to Discord (status line and card). On by default.
    public var discordFilter: CensorLevel {
        didSet { defaults.set(discordFilter.key, forKey: "discordFilter") }
    }

    public var ghost = false

    /// The window corner that stays put while the overlay changes size (the one
    /// nearest the screen's corner). Layouts are pinned to it during a morph.
    /// Not persisted.
    public var morphAnchor: Alignment = .topTrailing

    /// Discord Rich Presence on/off (menu). Off until the user turns it on.
    public var discordEnabled: Bool {
        didSet { defaults.set(discordEnabled, forKey: "discordEnabled") }
    }

    /// The Discord status line: "Listening to" the artist, the song title or the current lyric (menu).
    public var discordStatusLine: DiscordStatusLine {
        didSet { defaults.set(discordStatusLine.rawValue, forKey: "discordStatusLine") }
    }

    /// Overrides the Discord application the card belongs to (development only:
    /// `defaults write com.bunkhq.overtone discordAppID …`). Unset uses Overtone's own.
    public let discordAppID: String?

    /// Last overlay frame, in global screen coordinates.
    public var overlayFrame: CGRect? {
        get { defaults.string(forKey: "overlayFrame").map(NSRectFromString).flatMap { $0.isEmpty ? nil : $0 } }
        set { defaults.set(newValue.map(NSStringFromRect), forKey: "overlayFrame") }
    }

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        let stored = defaults.string(forKey: "overlaySize").map(NSSizeFromString).flatMap { $0.width > 0 && $0.height > 0 ? $0 : nil }
        // Before free sizing, a chosen layout with a size each: start from that layout's preset.
        let legacy = defaults.string(forKey: "layout").flatMap(OverlayLayout.init) ?? .card
        overlaySize = OverlayLayout.clamp(stored ?? legacy.preset)
        overlayVisible = defaults.object(forKey: "overlayVisible") as? Bool ?? true
        tone = defaults.string(forKey: "tone").flatMap(TonePreference.init) ?? .auto
        background = defaults.string(forKey: "background").flatMap(OverlayBackground.init).flatMap { OverlayBackground.available.contains($0) ? $0 : nil } ?? .cover
        musicOnly = defaults.object(forKey: "musicOnly") as? Bool ?? true
        colorMenuBarIcon = defaults.bool(forKey: "colorMenuBarIcon")
        discordFilter = CensorLevel.ordered.first { $0.key == defaults.string(forKey: "discordFilter") } ?? .all
        discordEnabled = defaults.bool(forKey: "discordEnabled")
        discordStatusLine = defaults.string(forKey: "discordStatusLine").flatMap(DiscordStatusLine.init) ?? .artist
        discordAppID = defaults.string(forKey: "discordAppID")
        for key in ["layout"] + OverlayLayout.allCases.map({ "size.\($0.rawValue)" }) { defaults.removeObject(forKey: key) }
    }
}
