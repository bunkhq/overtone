import AppKit
import SwiftUI

/// The floating overlay window: borderless, transparent, above other windows,
/// on every Space and over full-screen apps, and it never takes focus.
final class OverlayPanel: NSPanel {
    init() {
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        isFloatingPanel = true
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        isMovable = false // dragging is handled by the overlay, so it can snap on release
        isReleasedWhenClosed = false
        animationBehavior = .none
        acceptsMouseMovedEvents = true
        setAccessibilitySubrole(.floatingWindow)
        setAccessibilityLabel("Overtone")
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Clicks reach the controls on the first click, even though the app is never active.
final class OverlayHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    required init(rootView: Content) {
        super.init(rootView: rootView)
        sizingOptions = [] // the controller owns the window size
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }
}

/// Resize zones along the overlay's edges and corners, in AppKit rather than
/// SwiftUI: an always-active tracking area sets the resize cursor even though
/// the app is never frontmost, and a mouse-tracking loop runs the drag so it
/// can't be stolen by the overlay's move gesture. Elsewhere it is transparent
/// to clicks.
final class ResizeFrameView: NSView {
    static let edge: CGFloat = 8
    static let corner: CGFloat = 18

    var onResizeBegan: (() -> Void)?
    var onResize: ((ResizeEdges) -> Void)?
    var onResizeEnded: (() -> Void)?
    private var tracking: NSTrackingArea?
    private var showingResizeCursor = false

    override var isFlipped: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// Which edges a point (view coordinates, y up) would resize.
    static func edges(at p: NSPoint, in b: NSRect) -> ResizeEdges {
        guard b.insetBy(dx: -1, dy: -1).contains(p) else { return [] }
        let left = p.x < b.minX + edge, right = p.x > b.maxX - edge
        let bottom = p.y < b.minY + edge, top = p.y > b.maxY - edge
        let nearL = p.x < b.minX + corner, nearR = p.x > b.maxX - corner
        let nearB = p.y < b.minY + corner, nearT = p.y > b.maxY - corner
        // Corners get a larger zone: they're the easiest handles to aim for.
        if (left || bottom) && nearL && nearB { return [.left, .bottom] }
        if (left || top) && nearL && nearT { return [.left, .top] }
        if (right || bottom) && nearR && nearB { return [.right, .bottom] }
        if (right || top) && nearR && nearT { return [.right, .top] }
        var e: ResizeEdges = []
        if left { e.insert(.left) }
        if right { e.insert(.right) }
        if bottom { e.insert(.bottom) }
        if top { e.insert(.top) }
        return e
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let p = convert(point, from: superview)
        return Self.edges(at: p, in: bounds).isEmpty ? nil : self
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let t = NSTrackingArea(rect: .zero, options: [.activeAlways, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseMoved(with event: NSEvent) { updateCursor(event) }
    override func mouseEntered(with event: NSEvent) { updateCursor(event) }

    override func mouseExited(with event: NSEvent) {
        if showingResizeCursor {
            NSCursor.arrow.set()
            showingResizeCursor = false
        }
    }

    private func updateCursor(_ event: NSEvent) {
        let edges = Self.edges(at: convert(event.locationInWindow, from: nil), in: bounds)
        if edges.isEmpty {
            if showingResizeCursor { NSCursor.arrow.set() }
            showingResizeCursor = false
        } else {
            Self.cursor(for: edges).set()
            showingResizeCursor = true
        }
    }

    override func mouseDown(with event: NSEvent) {
        let edges = Self.edges(at: convert(event.locationInWindow, from: nil), in: bounds)
        guard !edges.isEmpty, let window else { return super.mouseDown(with: event) }
        let cursor = Self.cursor(for: edges)
        cursor.set()
        onResizeBegan?()
        while let next = window.nextEvent(matching: [.leftMouseDragged, .leftMouseUp]) {
            if next.type == .leftMouseUp { break }
            onResize?(edges)
            cursor.set()
        }
        onResizeEnded?()
        showingResizeCursor = true
    }

    static func cursor(for edges: ResizeEdges) -> NSCursor {
        if #available(macOS 15, *) {
            let position: NSCursor.FrameResizePosition = switch edges {
            case .left: .left
            case .right: .right
            case .top: .top
            case .bottom: .bottom
            case [.top, .left]: .topLeft
            case [.top, .right]: .topRight
            case [.bottom, .left]: .bottomLeft
            default: .bottomRight
            }
            return NSCursor.frameResize(position: position, directions: .all)
        }
        return edges.contains(.left) || edges.contains(.right) ? .resizeLeftRight : .resizeUpDown
    }
}

/// macOS only honours cursor changes from the frontmost app. The overlay is
/// never frontmost (it must not steal focus), so opt this connection in to
/// setting the cursor from the background. Private WindowServer API, like the
/// MediaRemote access this app already depends on; if it ever disappears the
/// resize zones still work, just without the cursor hint.
enum BackgroundCursor {
    private typealias DefaultConnection = @convention(c) () -> Int32
    private typealias SetProperty = @convention(c) (Int32, Int32, CFString, CFTypeRef) -> Int32

    static func enable() {
        guard let handle = dlopen(nil, RTLD_NOW),
              let conn = dlsym(handle, "_CGSDefaultConnection"),
              let set = dlsym(handle, "CGSSetConnectionProperty")
        else { return }
        let cid = unsafeBitCast(conn, to: DefaultConnection.self)()
        _ = unsafeBitCast(set, to: SetProperty.self)(cid, cid, "SetsCursorInBackground" as CFString, kCFBooleanTrue)
    }
}

/// Where the overlay settles: magnetic snap to the nearest edge or corner.
/// Pure geometry in screen coordinates (origin bottom-left), so it's testable.
enum SnapGeometry {
    static let margin: CGFloat = 14
    /// Within this distance of an edge, the overlay snaps to it.
    static let reach: CGFloat = 120

    static func settle(_ frame: CGRect, in visible: CGRect) -> CGRect {
        let area = visible.insetBy(dx: margin, dy: margin)
        var f = clamp(frame, in: area)
        let left = f.minX - area.minX, right = area.maxX - f.maxX
        let bottom = f.minY - area.minY, top = area.maxY - f.maxY
        let dx = min(left, right), dy = min(bottom, top)
        var snapX = dx <= reach, snapY = dy <= reach
        if !snapX && !snapY {
            // Always end on an edge: take whichever is nearer.
            if dx <= dy { snapX = true } else { snapY = true }
        }
        if snapX { f.origin.x = left <= right ? area.minX : area.maxX - f.width }
        if snapY { f.origin.y = bottom <= top ? area.minY : area.maxY - f.height }
        return f
    }

    /// The frame for a new size, keeping the corner nearest the screen's corner fixed,
    /// so switching layouts doesn't walk the overlay away from its edge.
    static func resize(_ frame: CGRect, to size: CGSize, in visible: CGRect) -> CGRect {
        var f = CGRect(origin: frame.origin, size: size)
        if frame.midX > visible.midX { f.origin.x = frame.maxX - size.width }
        if frame.midY > visible.midY { f.origin.y = frame.maxY - size.height }
        return clamp(f, in: visible.insetBy(dx: margin, dy: margin))
    }

    static func clamp(_ frame: CGRect, in area: CGRect) -> CGRect {
        var f = frame
        f.origin.x = min(max(f.minX, area.minX), max(area.minX, area.maxX - f.width))
        f.origin.y = min(max(f.minY, area.minY), max(area.minY, area.maxY - f.height))
        return f
    }

    /// Default spot for a first launch: top-right corner, below the menu bar.
    static func initial(size: CGSize, in visible: CGRect) -> CGRect {
        CGRect(x: visible.maxX - margin - size.width, y: visible.maxY - margin - size.height, width: size.width, height: size.height)
    }
}

extension OverlayLayout {
    /// Preset sizes (menu, hotkey, double-click), as on Windows. From any of them the user resizes freely.
    var preset: CGSize {
        switch self {
        case .pill: CGSize(width: 400, height: 48)
        case .card: CGSize(width: 340, height: 150)
        case .lyrics: CGSize(width: 360, height: 232)
        }
    }

    static let minSize = CGSize(width: 280, height: 44)
    static let maxSize = CGSize(width: 720, height: 600)

    static func clamp(_ size: CGSize) -> CGSize {
        CGSize(width: min(max(size.width, minSize.width), maxSize.width), height: min(max(size.height, minSize.height), maxSize.height))
    }

    /// The lyric panel folded to its footer row for a song without lyrics: the
    /// cover with the same 16 pt above, below and to its left.
    static let foldedLyricsHeight: CGFloat = 66
    /// The player card's lyric row (an 18 pt line and its 6 pt gap).
    static let cardLyricRow: CGFloat = 24

    /// The size as drawn: the user's size, folded where a song has no lyrics
    /// (the card drops its lyric line, the lyric panel keeps only its footer).
    /// The layout still comes from the stored size, so the next song with lyrics unfolds.
    static func drawnSize(user: CGSize, lyricsSpace: Bool) -> CGSize {
        guard !lyricsSpace else { return user }
        switch forHeight(user.height) {
        case .pill: return user
        case .card: return CGSize(width: user.width, height: user.height - cardLyricRow)
        case .lyrics: return CGSize(width: user.width, height: foldedLyricsHeight)
        }
    }

    /// The stored size for a height the user drags to, ported from Windows'
    /// ApplyDrawnSize. With lyrics they're the same. Without, the dragged edge stays
    /// under the pointer: below the card's threshold it's a pill; above it, the card
    /// without its lyric line (the stored height keeps that row, so it returns with
    /// the next song that has lyrics), up to the tallest such card.
    static func userSize(drawn: CGSize, lyricsSpace: Bool) -> CGSize {
        guard !lyricsSpace else { return drawn }
        let h = min(drawn.height, maxDrawnHeight(lyricsSpace: false))
        return CGSize(width: drawn.width, height: h >= cardFrom - cardLyricRow ? h + cardLyricRow : h)
    }

    /// Without lyrics the lyric panel has nothing to show, so a drag stops at the tallest card.
    static func maxDrawnHeight(lyricsSpace: Bool) -> CGFloat {
        lyricsSpace ? maxSize.height : lyricsFrom - cardLyricRow - 1
    }

    /// Corner radius at height `h`. A pill's corners grow with its height (14 at
    /// 48, 26 at the tallest), so a big cover inside it stays concentric.
    func cornerRadius(height h: CGFloat) -> CGFloat {
        self == .pill ? min(max((h * 0.29).rounded(), 14), 26) : 16
    }
}

/// Which edges a resize drag moves.
struct ResizeEdges: OptionSet, Hashable, Sendable {
    let rawValue: Int
    static let left = ResizeEdges(rawValue: 1)
    static let right = ResizeEdges(rawValue: 2)
    static let top = ResizeEdges(rawValue: 4)
    static let bottom = ResizeEdges(rawValue: 8)
}

extension SnapGeometry {
    /// `start` resized by a mouse movement `delta` (screen coordinates, y up),
    /// moving only `edges`; the opposite edges stay put.
    static func resized(_ start: CGRect, edges: ResizeEdges, by delta: CGVector, min lo: CGSize, max hi: CGSize) -> CGRect {
        func w(_ v: CGFloat) -> CGFloat { min(max(v, lo.width), hi.width) }
        func h(_ v: CGFloat) -> CGFloat { min(max(v, lo.height), hi.height) }
        var f = start
        if edges.contains(.right) { f.size.width = w(start.width + delta.dx) }
        if edges.contains(.left) {
            f.size.width = w(start.width - delta.dx)
            f.origin.x = start.maxX - f.width
        }
        if edges.contains(.top) { f.size.height = h(start.height + delta.dy) }
        if edges.contains(.bottom) {
            f.size.height = h(start.height - delta.dy)
            f.origin.y = start.maxY - f.height
        }
        return f
    }
}
