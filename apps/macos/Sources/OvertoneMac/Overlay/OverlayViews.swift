import AppKit
import OvertoneCore
import SwiftUI

/// What the overlay can ask the app to do.
struct OverlayActions {
    var send: (TransportCommand) -> Void
    /// Seeks to a position in ms (updating the overlay at once).
    var seek: (UInt64) -> Void
    var cycleLayout: () -> Void
    var dragChanged: () -> Void
    var dragEnded: () -> Void
}

/// Motion tokens (docs/ARCHITECTURE.md §6). Reduce Motion swaps springs for short fades.
enum Motion {
    static func lyric(_ reduce: Bool) -> Animation { reduce ? .easeOut(duration: 0.15) : .spring(response: 0.5, dampingFraction: 0.92) }
    static func morph(_ reduce: Bool) -> Animation { reduce ? .easeOut(duration: 0.15) : .spring(response: 0.42, dampingFraction: 0.86) }
    static let palette: Animation = .linear(duration: 0.6)
}

struct OverlayRoot: View {
    let model: NowPlayingModel
    let settings: Settings
    let actions: OverlayActions
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    /// The glass's appearance: the controller sets the window's appearance from the tone setting.
    @Environment(\.colorScheme) private var scheme

    private var colors: OverlayColors {
        settings.background == .glass && !reduceTransparency ? model.colors.onGlass(dark: scheme == .dark) : model.colors
    }

    var body: some View {
        let layout = settings.layout
        let radius = layout.cornerRadius(height: settings.overlaySize.height)
        // Each layout is laid out at its final size, not the window's animating one,
        // and pinned to the corner that stays put: as on Windows, the window's size
        // animates around fixed layouts, the old one fading out as the new fades in,
        // so nothing reflows mid-morph. While the user drags an edge the two match.
        let target = OverlayLayout.drawnSize(user: settings.overlaySize, lyricsSpace: model.lyricsSpace)
        GeometryReader { geo in
            ZStack {
                // The frame and transition belong to each layout, so the outgoing one
                // keeps its own size while it fades.
                switch layout {
                case .pill:
                    PillView(model: model, actions: actions, size: target)
                        .frame(width: target.width, height: target.height).transition(.opacity)
                case .card:
                    CardView(model: model, actions: actions, size: target)
                        .frame(width: target.width, height: target.height).transition(.opacity)
                case .lyrics:
                    LyricsPanelView(model: model, actions: actions, height: target.height)
                        .frame(width: target.width, height: target.height).transition(.opacity)
                }
            }
            .animation(Motion.morph(reduceMotion), value: layout)
            .frame(width: geo.size.width, height: geo.size.height, alignment: settings.morphAnchor)
        }
        .background { OverlayMaterial(backdrop: model.backdrop, radius: radius, background: settings.background) }
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        .contentShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        .gesture(
            DragGesture(minimumDistance: 3, coordinateSpace: .global)
                .onChanged { _ in actions.dragChanged() }
                .onEnded { _ in actions.dragEnded() }
        )
        .simultaneousGesture(TapGesture(count: 2).onEnded { actions.cycleLayout() })
        .environment(\.overlayBackground, settings.background)
        .modifier(PaletteEnvironment(colors))
        .animation(Motion.palette, value: colors)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Now playing")
    }
}

/// The record's own colours, blurred, under a palette scrim that keeps text legible.
struct OverlayMaterial: View {
    let backdrop: CGImage?
    let radius: CGFloat
    var background: OverlayBackground = .cover
    @Environment(\.overlayColors) private var colors
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        // Reduce Transparency turns see-through backgrounds solid, as the Windows OS switch does.
        let mode: OverlayBackground = reduceTransparency && (background == .cover || background == .glass) ? .solid : background
        Group {
            switch mode {
            case .cover: cover
            case .gradient:
                // Base warming toward the accent in the top-left corner (Windows' TintBrush).
                LinearGradient(stops: [.init(color: colors.corner.color, location: 0), .init(color: colors.base.color, location: 0.7)],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
            case .solid: colors.base.color
            case .glass:
                if #available(macOS 26, *) {
                    // The glass, then a measured layer of the track's hue at the glass's own
                    // lightness, so text holds its contrast whatever is behind (OverlayColors.onGlass).
                    ZStack {
                        GlassBackground(radius: radius, tint: colors.base.nsColor.withAlphaComponent(0.25))
                        colors.base.color.opacity(OverlayColors.glassScrim(dark: colors.tone == .dark))
                    }
                } else {
                    colors.base.color
                }
            }
        }
        .overlay {
            if mode != .glass { // Liquid Glass draws its own edge
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .strokeBorder(colors.ink.color.opacity(0.06), lineWidth: 1)
            }
        }
        .overlay(alignment: .top) {
            if mode != .glass {
                // Top edge highlight (concept: inset 0 1px ink 14%).
                colors.ink.color.opacity(0.14).frame(height: 1).padding(.horizontal, radius * 0.6)
            }
        }
    }

    private var cover: some View {
        ZStack {
            if let backdrop {
                Image(decorative: backdrop, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .id(ObjectIdentifier(backdrop))
                    .transition(.opacity)
            }
            colors.base.color.opacity(backdrop == nil ? 1 : 0.8)
        }
        .animation(Motion.palette, value: backdrop.map(ObjectIdentifier.init))
    }
}

/// Native Liquid Glass behind the overlay (macOS 26+), tinted with the track's
/// base colour so the palette's inks, solved against that colour, stay legible.
@available(macOS 26, *)
struct GlassBackground: NSViewRepresentable {
    var radius: CGFloat
    var tint: NSColor

    func makeNSView(context: Context) -> NSGlassEffectView {
        let view = NSGlassEffectView()
        view.style = .regular
        return view
    }

    func updateNSView(_ view: NSGlassEffectView, context: Context) {
        view.cornerRadius = radius
        view.tintColor = tint
    }
}

struct ArtView: View {
    let image: CGImage?
    let size: CGFloat
    let radius: CGFloat
    @Environment(\.overlayColors) private var colors

    var body: some View {
        ZStack {
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .id(ObjectIdentifier(image))
                    .transition(.opacity)
            } else {
                colors.ink.color.opacity(0.1)
                Image(systemName: "music.note")
                    .font(.system(size: size * 0.38, weight: .semibold))
                    .foregroundStyle(colors.inkSoft.color)
            }
        }
        .animation(.easeInOut(duration: 0.3), value: image.map(ObjectIdentifier.init))
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
        .shadow(color: .black.opacity(0.4), radius: 5, y: 4)
        .accessibilityHidden(true)
    }
}

struct ControlButton: View {
    let symbol: String
    let label: String
    var size: CGFloat = 32
    var iconSize: CGFloat = 15
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: iconSize, weight: .semibold))
                .frame(width: size, height: size)
                .contentShape(Rectangle())
        }
        .buttonStyle(OverlayButtonStyle(radius: size * 0.31))
        .accessibilityLabel(label)
    }
}

/// A soft wash on hover plus, as on Windows, a white shine that follows the
/// cursor. Liquid Glass (the native look) keeps the plain wash, as Windows'
/// native look does.
struct OverlayButtonStyle: ButtonStyle {
    let radius: CGFloat
    @Environment(\.overlayColors) private var colors
    @Environment(\.overlayBackground) private var background
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @State private var hovering = false
    @State private var pointer: CGPoint?

    /// Windows' ShineCore: white at 22% in the middle, gone at 70% of the button's size.
    static let shineAlpha = 0.22
    static let shineReach: CGFloat = 0.7

    func makeBody(configuration: Configuration) -> some View {
        let shine = background != .glass && !reduceTransparency
        configuration.label
            .foregroundStyle(colors.ink.color)
            .background(
                RoundedRectangle(cornerRadius: radius, style: .continuous)
                    .fill(colors.ink.color.opacity(configuration.isPressed ? 0.18 : hovering ? 0.12 : 0))
            )
            .overlay {
                if shine {
                    GeometryReader { geo in
                        let p = pointer ?? CGPoint(x: geo.size.width / 2, y: geo.size.height / 2)
                        RadialGradient(
                            colors: [.white.opacity(Self.shineAlpha), .white.opacity(0)],
                            center: UnitPoint(x: p.x / max(geo.size.width, 1), y: p.y / max(geo.size.height, 1)),
                            startRadius: 0, endRadius: max(geo.size.width, geo.size.height) * Self.shineReach
                        )
                        .clipShape(RoundedRectangle(cornerRadius: radius, style: .continuous))
                        .opacity(hovering ? 1 : 0)
                    }
                    .allowsHitTesting(false)
                }
            }
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.16), value: hovering)
            .onContinuousHover { phase in
                switch phase {
                case let .active(location):
                    pointer = location
                    hovering = true
                case .ended:
                    hovering = false
                }
            }
    }
}

extension EnvironmentValues {
    @Entry var overlayBackground: OverlayBackground = .cover
}

struct TransportControls: View {
    let playing: Bool
    let actions: OverlayActions
    /// Play/pause alone, for when previous and next would leave the text too little room.
    var compact = false
    var size: CGFloat = 32
    var iconSize: CGFloat = 15

    var body: some View {
        HStack(spacing: 2) {
            if !compact {
                ControlButton(symbol: "backward.fill", label: "Previous track", size: size, iconSize: iconSize - 2) { actions.send(.previous) }
            }
            ControlButton(symbol: playing ? "pause.fill" : "play.fill", label: playing ? "Pause" : "Play", size: size, iconSize: iconSize + 2) {
                actions.send(.togglePlayPause)
            }
            if !compact {
                ControlButton(symbol: "forward.fill", label: "Next track", size: size, iconSize: iconSize - 2) { actions.send(.next) }
            }
        }
    }
}

func formatTime(_ ms: UInt64) -> String {
    let s = ms / 1000
    return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
}

/// Elapsed time; ticks once a second only while playing.
struct ElapsedLabel: View {
    let model: NowPlayingModel
    let playing: Bool
    /// While scrubbing: the time under the pointer.
    var override: UInt64?

    var body: some View {
        if let override {
            Text(formatTime(override))
        } else if playing {
            TimelineView(.periodic(from: .now, by: 1)) { _ in Text(formatTime(model.positionMs())) }
        } else {
            Text(formatTime(model.positionMs()))
        }
    }
}

/// The line being sung, or nil between lines and without synced lyrics.
@MainActor
func currentLyric(_ model: NowPlayingModel) -> String? {
    guard case let .synced(lines) = model.lyrics, let i = model.lineIndex, lines.indices.contains(i), !lines[i].text.isEmpty else { return nil }
    return lines[i].text
}

// MARK: - Card

/// The full player, with a line of its own for the current lyric (as on Windows).
/// Shown from `OverlayLayout.cardFrom` to `lyricsFrom`; the cover grows with the height.
struct CardView: View {
    let model: NowPlayingModel
    let actions: OverlayActions
    let size: CGSize
    @Environment(\.overlayColors) private var colors
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var scrub: Double?

    /// The lyric line's row: an 18 pt line plus its 6 pt gap.
    static let lyricRow: CGFloat = 24

    /// Cover size at height `h`: 64 at the 150 pt preset, 40...120. Without a
    /// lyric line the cover takes that row's room too, so no gap opens below.
    static func art(height h: CGFloat, lyricLine: Bool) -> CGFloat {
        min(max(h - 86 + (lyricLine ? 0 : lyricRow), 40), 120).rounded()
    }

    /// Without lyrics the card drops its lyric line (the window folds to match).
    private var lyricLine: Bool { model.lyricsSpace }

    var body: some View {
        let info = model.info
        let line = lyricLine
        let art = Self.art(height: size.height, lyricLine: line)
        let lyric = currentLyric(model)
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                ArtView(image: model.artwork, size: art, radius: min(max(art * 0.12, 6), 12).rounded())
                VStack(alignment: .leading, spacing: 1) {
                    MarqueeText(text: info?.title ?? "Nothing playing", size: 16, weight: .semibold, kerning: -0.16, color: colors.ink.color)
                    if let artist = info?.artist, !artist.isEmpty {
                        MarqueeText(text: artist, size: 13, color: colors.inkSoft.color)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.trailing, 4)
            if line {
                ZStack(alignment: .leading) {
                    if let lyric {
                        // As on Windows: the new line fades in; a long one glides across once.
                        MarqueeText(text: lyric, size: 13, weight: .semibold, color: colors.ink.color,
                                    glide: .init(msToNext: model.msToNextLine(), reduceMotion: reduceMotion))
                            .id(lyric)
                            .transition(.asymmetric(insertion: .opacity.animation(.easeOut(duration: 0.24)), removal: .opacity.animation(.easeOut(duration: 0.26))))
                    }
                }
                .frame(maxWidth: .infinity, minHeight: 18, maxHeight: 18, alignment: .leading)
                .animation(.easeOut(duration: 0.24), value: lyric)
                .padding(.top, 6)
                .padding(.trailing, 4)
            }
            HStack(spacing: 8) {
                if let progress = model.progress, let info {
                    ElapsedLabel(model: model, playing: info.playing, override: scrub.map { UInt64($0 * Double(info.durationMs ?? 0)) })
                    ScrubBar(state: progress, track: colors.ink.color.opacity(0.16), fill: colors.accent.color,
                             height: 4, hoverScale: 1.5, durationMs: info.durationMs,
                             seek: model.canSeek ? actions.seek : nil, scrub: $scrub)
                    Text(formatTime(info.durationMs ?? 0))
                } else {
                    Spacer(minLength: 0)
                }
                TransportControls(playing: info?.playing ?? false, actions: actions, size: 32, iconSize: 16)
                    .padding(.leading, 2)
            }
            .font(.system(size: 11).monospacedDigit())
            .foregroundStyle(colors.inkSoft.color)
            .padding(.top, 6)
        }
        .padding(EdgeInsets(top: 16, leading: 16, bottom: 8, trailing: 8))
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }
}

// MARK: - Pill

/// How a pill lays out at a given size, ported from the Windows overlay. Three
/// pills by height: one line; the song over the lyric; and a tall pill whose
/// controls sit under the text beside the progress bar, so the song and a larger
/// lyric get the full width beside a cover that fills the height. A narrow pill
/// drops previous/next rather than cutting the text to a word or two.
struct PillMetrics: Equatable {
    static let inset: CGFloat = 6
    /// From this height the pill shows the song above the lyric.
    static let twoLineFrom: CGFloat = 78
    /// Least space between a tall pill's content and its top and bottom edges.
    static let tallInset: CGFloat = 11
    /// Narrowest the text column may get before the layout gives it room.
    static let minText: CGFloat = 150

    var art: CGFloat
    var lineFont: CGFloat
    var titleFont: CGFloat
    var twoLine: Bool
    var tall: Bool
    var compact: Bool

    /// A tall pill's content height for lyric size `font`: the song line and its gap,
    /// the lyric line, a 5 pt gap, the 28 pt controls row.
    static func tallContent(_ font: CGFloat) -> CGFloat { 17 + 2 + (font * 1.4).rounded(.up) + 5 + 28 }

    init(size: CGSize) {
        let w = size.width, h = size.height, inset = Self.inset
        func clamp(_ v: CGFloat, _ lo: CGFloat, _ hi: CGFloat) -> CGFloat { min(max(v, lo), hi) }
        twoLine = h >= Self.twoLineFrom
        // Text scales with the height so the lines fill the cover's height: the lyric
        // takes ~85-90% of the room, the song line follows at about 0.7x.
        let room = h - 2 * inset - 4
        let twoLineFont = clamp((room * 0.85 - 9) / 2.36, 13.5, 18)
        var tallFont = clamp((room * 0.9 - 35) / 2.33, 14.5, 26)
        let cover = clamp(h - 2 * inset, 32, 112)
        var besideWidth = w - 2 * inset - cover - 12 - 96
        let narrow = besideWidth < Self.minText
        tall = Self.tallContent(14.5) + 2 * (narrow ? inset : Self.tallInset) <= h
        let tallText = w - 2 * inset - cover - 12 - 4
        compact = tall ? tallText - 10 - 80 < 80 : narrow
        if compact && !tall { besideWidth += 64 }
        art = cover.rounded()
        // Beside a narrow column the text stays modest so it can't outgrow it. One
        // line grows from 13.5 at the shortest pill to the size the two-line pill
        // starts at, so switching between them keeps the lyric's size.
        let widthCap = max(13.5, besideWidth / 11)
        tallFont = min(tallFont, max(14.5, tallText / 11))
        let twoLineStart = min(clamp(((Self.twoLineFrom - 2 * inset - 4) * 0.85 - 9) / 2.36, 13.5, 18), widthCap)
        let minH = OverlayLayout.minSize.height
        let oneLineFont = 13.5 + (clamp(h, minH, Self.twoLineFrom) - minH) / (Self.twoLineFrom - minH) * (twoLineStart - 13.5)
        let line = tall ? tallFont : twoLine ? min(twoLineFont, widthCap) : oneLineFont
        lineFont = (line * 2).rounded() / 2
        titleFont = twoLine ? (clamp(lineFont * (tall ? 0.68 : 0.72), 11.5, 15) * 2).rounded() / 2 : 11.5
    }
}

struct PillView: View {
    let model: NowPlayingModel
    let actions: OverlayActions
    let size: CGSize
    @Environment(\.overlayColors) private var colors
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var scrub: Double?

    /// The Windows pill's line change: the new line rises 10 pt into place and
    /// fades in while the old one rises out and fades away.
    static let lineSwap = AnyTransition.asymmetric(
        insertion: .offset(y: 10).animation(.easeOut(duration: 0.34)).combined(with: .opacity.animation(.easeOut(duration: 0.26))),
        removal: .offset(y: -10).animation(.easeOut(duration: 0.34)).combined(with: .opacity.animation(.easeOut(duration: 0.18)))
    )

    var body: some View {
        let m = PillMetrics(size: size)
        let info = model.info
        let lyric = currentLyric(model)
        let title = info.map { $0.title.isEmpty ? "Untitled" : $0.title } ?? "Nothing playing"
        let artist = info?.artist ?? ""
        let songLine = artist.isEmpty ? title : "\(title) · \(artist)"
        // Main line: the lyric; between lines, the song (just the title when the line above names the artist).
        let line = lyric ?? (m.twoLine ? title : songLine)
        // Line above: "title · artist" over a lyric, the artist alone over the title.
        let above = lyric == nil ? artist : songLine
        let radius = OverlayLayout.pill.cornerRadius(height: size.height)
        HStack(spacing: 12) {
            ArtView(image: model.artwork, size: m.art, radius: radius - PillMetrics.inset)
            VStack(alignment: .leading, spacing: 0) {
                if m.twoLine {
                    MarqueeText(text: above, size: m.titleFont, color: colors.inkSoft.color)
                        .padding(.bottom, 2)
                    Spacer(minLength: 0)
                }
                ZStack(alignment: .leading) {
                    MarqueeText(text: line, size: m.lineFont, weight: .semibold, color: colors.ink.color,
                                glide: .init(msToNext: model.msToNextLine(), reduceMotion: reduceMotion))
                        .id(line)
                        .transition(reduceMotion ? .opacity : Self.lineSwap)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .clipped() // the rising lines stay inside the line's slot
                .animation(.easeOut(duration: 0.34), value: line)
                // Optical centring: a lyric's weight sits low in its box, so lift it between the lines around it.
                .offset(y: m.twoLine ? -(m.lineFont * 0.1).rounded() : 0)
                .accessibilityLabel(line)
                if m.twoLine { Spacer(minLength: 0) }
                HStack(spacing: 10) {
                    Group {
                        if let progress = model.progress {
                            ScrubBar(state: progress, track: colors.ink.color.opacity(0.12), fill: colors.accent.color,
                                     height: 2, hoverScale: 2, durationMs: info?.durationMs,
                                     seek: model.canSeek ? actions.seek : nil, scrub: $scrub)
                        } else {
                            Color.clear
                        }
                    }
                    .frame(height: 2)
                    if m.tall {
                        TransportControls(playing: info?.playing ?? false, actions: actions, compact: m.compact, size: 28, iconSize: 14)
                            .padding(.trailing, -4)
                    }
                }
                .padding(.top, 5)
            }
            .padding(.top, m.twoLine ? 2 : 0)
            .padding(.bottom, m.twoLine ? (m.tall ? 0 : 3) : 0)
            .frame(maxHeight: m.twoLine ? .infinity : nil)
            if !m.tall {
                TransportControls(playing: info?.playing ?? false, actions: actions, compact: m.compact, size: 32, iconSize: 15)
            }
        }
        .padding(EdgeInsets(top: 6, leading: 6, bottom: 6, trailing: m.tall ? 10 : 4))
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}

// MARK: - Lyrics panel

struct LyricsPanelView: View {
    let model: NowPlayingModel
    let actions: OverlayActions
    let height: CGFloat
    @Environment(\.overlayColors) private var colors

    var body: some View {
        // A song without lyrics folds the panel to its footer row: no empty window, no divider.
        let fold = !model.lyricsSpace
        VStack(spacing: 0) {
            if !fold {
                // Takes all the height the footer leaves: a taller panel shows more lines.
                LyricsWindow(lyrics: model.lyrics, index: model.lineIndex, seek: model.canSeek ? actions.seek : nil)
                    // Room either side for the hover wash, which overhangs the text.
                    .padding(.horizontal, -LyricsWindow.overhang)
                    .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity)
                    .transition(.opacity)
                colors.ink.color.opacity(0.1).frame(height: 1).padding(.top, 12)
            }
            HStack(spacing: 10) {
                ArtView(image: model.artwork, size: 34, radius: 7)
                VStack(alignment: .leading, spacing: 0) {
                    MarqueeText(text: model.info?.title ?? "Nothing playing", size: 13, weight: .semibold, color: colors.ink.color)
                    if let artist = model.info?.artist, !artist.isEmpty {
                        MarqueeText(text: artist, size: 12, color: colors.inkSoft.color)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                TransportControls(playing: model.info?.playing ?? false, actions: actions, size: 28, iconSize: 13)
                    .padding(.trailing, -4)
            }
            .padding(.top, fold ? 0 : 12)
        }
        // Folded, the cover sits as far from the bottom and left edges as from the top.
        .padding(.leading, fold ? 16 : 18)
        .padding(.trailing, 16)
        .padding(.top, 16)
        .padding(.bottom, fold ? 16 : 14)
        .frame(maxHeight: .infinity, alignment: fold ? .center : .top)
        .animation(.easeOut(duration: 0.2), value: fold)
    }
}

/// The lyric list. Lines wrap rather than truncate, so they vary in height;
/// each is measured, then the column is offset so the current line sits in the
/// middle. The offset is clamped, so the first line pins to the top and the
/// last to the bottom and the window never shows empty space.
struct LyricsWindow: View {
    static let overhang: CGFloat = 8
    let lyrics: LyricsState
    let index: Int?
    /// Set when lines can be clicked to seek (synced lyrics, a player with a timeline).
    var seek: ((UInt64) -> Void)?
    @State private var heights: [String: CGFloat] = [:]
    @State private var viewport: CGFloat = 0
    @State private var scroll = LyricScroll()
    @State private var hovered: Int?
    @Environment(\.overlayColors) private var colors
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        switch lyrics {
        case let .synced(lines) where !lines.isEmpty:
            synced(lines)
        case .loading:
            message("Finding lyrics…")
        case .instrumental:
            message("Instrumental", dots: true)
        case .unsynced:
            message("Lyrics aren't synced for this track")
        default:
            message("No lyrics for this track")
        }
    }

    /// How far to scroll the column so line `index` is centred, clamped to the content.
    static func offset(heights: [CGFloat], index: Int?, viewport: CGFloat) -> CGFloat {
        let total = heights.reduce(0, +)
        guard total > viewport, let index, heights.indices.contains(index) else { return 0 }
        let center = heights[..<index].reduce(0, +) + heights[index] / 2
        return min(max(center - viewport / 2, 0), total - viewport)
    }

    private static func key(_ i: Int, _ line: LyricLine) -> String { "\(i)\u{1f}\(line.startMs)\u{1f}\(line.text)" }

    private func synced(_ lines: [LyricLine]) -> some View {
        // Row heights as they'll be once the current line is at full size, so the
        // scroll target is right while rows are still animating.
        let hs = lines.indices.map { i in
            LyricLineView.rowHeight(text: heights[Self.key(i, lines[i])] ?? 22, current: i == index)
        }
        let total = hs.reduce(0, +)
        let follow = Self.offset(heights: hs, index: index, viewport: viewport)
        let maxOffset = max(0, total - viewport)
        // The wheel takes over from following the song; it hands back after a pause.
        let offset = min(scroll.userOffset ?? follow, maxOffset)
        let atTop = offset < 0.5, atBottom = offset > total - viewport - 0.5
        let fade = viewport > 0 ? min(0.3, 18 / viewport) : 0
        return VStack(alignment: .leading, spacing: 0) {
            ForEach(lines.indices, id: \.self) { i in
                let line = lines[i]
                let link = seek != nil && !line.text.isEmpty // instrumental gaps aren't links
                LyricLineView(text: line.text, current: i == index) { h in
                    if heights.count > 4000 { heights.removeAll() }
                    heights[Self.key(i, line)] = h
                }
                    .padding(.horizontal, Self.overhang)
                    .background {
                        // A synced line is a link to its timestamp: a soft wash on hover.
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(colors.ink.color.opacity(link && hovered == i ? 0.09 : 0))
                            .padding(.horizontal, 2)
                            .animation(.easeOut(duration: hovered == i ? 0.12 : 0.16), value: hovered == i)
                    }
                    .contentShape(Rectangle())
                    .onHover { inside in
                        if inside { hovered = i } else if hovered == i { hovered = nil }
                        (inside && link ? NSCursor.pointingHand : NSCursor.arrow).set()
                    }
                    .onTapGesture {
                        guard link, let seek else { return }
                        scroll.follow()
                        hovered = nil
                        seek(line.startMs)
                    }
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(line.text.isEmpty ? "Instrumental" : line.text)
                    .accessibilityAddTraits(link ? .isButton : [])
                    .accessibilityAction { if link { seek?(line.startMs) } }
            }
        }
        .fixedSize(horizontal: false, vertical: true) // every row at its own height, never squeezed
        .offset(y: -offset)
        .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .topLeading)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { viewport = $0 }
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { scroll.frame = $0 }
        .onChange(of: follow, initial: true) { scroll.followOffset = follow }
        .onChange(of: maxOffset, initial: true) { scroll.maxOffset = maxOffset }
        .onAppear { scroll.start() }
        .onDisappear { scroll.stop() }
        .clipped()
        .mask(
            LinearGradient(stops: [
                .init(color: .black.opacity(atTop ? 1 : 0), location: 0),
                .init(color: .black, location: fade),
                .init(color: .black, location: 1 - fade),
                .init(color: .black.opacity(atBottom ? 1 : 0), location: 1),
            ], startPoint: .top, endPoint: .bottom)
        )
        .animation(Motion.lyric(reduceMotion), value: index)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Lyrics")
    }

    private func message(_ text: String, dots: Bool = false) -> some View {
        HStack(spacing: 8) {
            if dots { BreathingDots() }
            Text(text).fixedSize(horizontal: false, vertical: true)
        }
        .font(.system(size: 14, weight: .medium))
        .foregroundStyle(colors.inkFaint.color)
        .frame(maxWidth: .infinity, minHeight: 0, maxHeight: .infinity, alignment: .leading)
    }
}

struct LyricLineView: View {
    /// Inactive lines are drawn at this scale; the current one at full size.
    static let dimScale: CGFloat = 0.8
    /// The same air above and below every lyric, however many lines it wraps to.
    static let padding: CGFloat = 8
    /// Height of an instrumental gap's dots row.
    static let dotsHeight: CGFloat = 22

    let text: String
    let current: Bool
    /// Reports the full-size height of the text (wrapped at the row's width).
    var onTextHeight: (CGFloat) -> Void = { _ in }
    @Environment(\.overlayColors) private var colors

    var body: some View {
        let scale = current ? 1 : Self.dimScale
        ScaledHeight(scale: scale) {
            Group {
                if text.isEmpty {
                    BreathingDots(animating: current)
                        .frame(height: Self.dotsHeight)
                } else {
                    Text(text)
                        .font(.system(size: 17, weight: .semibold))
                        .kerning(-0.17)
                        .lineSpacing(1)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true) // wrap, never truncate
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { onTextHeight($0) }
            .foregroundStyle(current ? colors.ink.color : colors.inkFaint.color)
            .scaleEffect(scale, anchor: .topLeading)
        }
        .padding(.vertical, Self.padding)
    }

    /// The row's height for a text of full-size height `text`.
    static func rowHeight(text: CGFloat, current: Bool) -> CGFloat {
        text * (current ? 1 : dimScale) + 2 * padding
    }
}

/// Takes the height its content has *as drawn*: the content's natural height
/// times `scale` (the content scales itself from its top-left). Animatable, so
/// a row grows and shrinks with its text instead of keeping its full-size
/// height and leaving uneven gaps (or squeezing a long lyric into its neighbour).
struct ScaledHeight: Layout {
    var scale: CGFloat

    var animatableData: CGFloat {
        get { scale }
        set { scale = newValue }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard let child = subviews.first else { return .zero }
        let natural = child.sizeThatFits(ProposedViewSize(width: proposal.width, height: nil))
        return CGSize(width: proposal.width ?? natural.width, height: natural.height * scale)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let child = subviews.first else { return }
        let natural = child.sizeThatFits(ProposedViewSize(width: bounds.width, height: nil))
        child.place(at: bounds.origin, anchor: .topLeading, proposal: ProposedViewSize(width: bounds.width, height: natural.height))
    }
}

/// Instrumental gap: three dots breathing. Static under Reduce Motion.
struct BreathingDots: View {
    var animating = true
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        if animating && !reduceMotion {
            TimelineView(.animation(minimumInterval: 1 / 30)) { ctx in
                dots(phase: ctx.date.timeIntervalSinceReferenceDate)
            }
        } else {
            dots(phase: nil)
        }
    }

    private func dots(phase: Double?) -> some View {
        HStack(spacing: 5) {
            ForEach(0..<3) { i in
                let t = phase.map { (sin(($0 - Double(i) * 0.2) * 2 * .pi / 1.6) + 1) / 2 } ?? 1
                Circle()
                    .frame(width: 6, height: 6)
                    .opacity(0.35 + 0.65 * t)
                    .scaleEffect(0.7 + 0.3 * t)
            }
        }
        .accessibilityLabel("Instrumental")
    }
}

