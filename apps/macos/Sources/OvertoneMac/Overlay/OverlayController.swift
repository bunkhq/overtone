import AppKit
import Observation
import SwiftUI

/// Owns the overlay window: showing and hiding it, sizing it for the layout,
/// dragging with magnetic snap, ghost mode, and remembering where it sits.
@MainActor
final class OverlayController {
    private let panel = OverlayPanel()
    private let model: NowPlayingModel
    private let settings: Settings
    private let send: (TransportCommand) -> Void

    private var dragStart: (origin: CGPoint, mouse: CGPoint)?
    private var resizeStart: (frame: CGRect, mouse: CGPoint)?
    private var shown = false
    private var screenObserver: NSObjectProtocol?

    static let ghostAlpha: CGFloat = 0.55

    init(model: NowPlayingModel, settings: Settings, send: @escaping (TransportCommand) -> Void) {
        self.model = model
        self.settings = settings
        self.send = send

        let actions = OverlayActions(
            send: send,
            seek: { [weak model] ms in
                model?.seekLocally(to: ms)
                send(.seek(ms: ms))
            },
            cycleLayout: { [weak settings] in settings?.cycleLayout() },
            dragChanged: { [weak self] in self?.dragChanged() },
            dragEnded: { [weak self] in self?.dragEnded() }
        )
        let host = OverlayHostingView(rootView: OverlayRoot(model: model, settings: settings, actions: actions))
        let resizer = ResizeFrameView()
        resizer.onResizeBegan = { [weak self] in self?.resizeBegan() }
        resizer.onResize = { [weak self] in self?.resizeChanged($0) }
        resizer.onResizeEnded = { [weak self] in self?.resizeEnded() }
        let container = NSView()
        for v in [host, resizer] as [NSView] {
            v.frame = container.bounds
            v.autoresizingMask = [.width, .height]
            container.addSubview(v)
        }
        panel.contentView = container
        BackgroundCursor.enable()

        let size = settings.overlaySize
        let frame = settings.overlayFrame.map { CGRect(origin: $0.origin, size: size) }
            ?? SnapGeometry.initial(size: size, in: Self.visibleFrame(containing: nil))
        panel.setFrame(SnapGeometry.clamp(frame, in: Self.visibleFrame(containing: frame).insetBy(dx: 8, dy: 8)), display: false)

        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.screensChanged() }
        }
        observe()
    }

    // MARK: State → window

    private func observe() {
        withObservationTracking {
            sync()
        } onChange: { [weak self] in
            Task { @MainActor in self?.observe() }
        }
    }

    private func sync() {
        let wantVisible = settings.overlayVisible && model.info != nil
        let ghost = settings.ghost

        // A preset (menu, hotkey, double-click) morphs the window to its size.
        // While the user drags an edge the window leads and the size follows it.
        let size = OverlayLayout.drawnSize(user: settings.overlaySize, lyricsSpace: model.lyricsSpace)
        if resizeStart == nil, abs(panel.frame.width - size.width) > 0.5 || abs(panel.frame.height - size.height) > 0.5 {
            resize(to: size)
        }
        // Liquid Glass takes its light or dark look from the window's appearance, and
        // the text follows it (OverlayColors.onGlass). The tone setting picks it:
        // the system's, the cover's, or forced. Other backgrounds draw their own.
        let glass = settings.background == .glass && !NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency
        let appearance: NSAppearance.Name?
        switch settings.tone {
        case _ where !glass, .system: appearance = nil
        case .dark: appearance = .darkAqua
        case .light: appearance = .aqua
        case .auto: appearance = model.colors.tone == .dark ? .darkAqua : .aqua
        }
        if panel.appearance?.name != appearance { panel.appearance = appearance.flatMap(NSAppearance.init(named:)) }
        panel.ignoresMouseEvents = ghost
        let alpha: CGFloat = ghost ? Self.ghostAlpha : 1
        if wantVisible != shown {
            shown = wantVisible
            wantVisible ? fadeIn(to: alpha) : fadeOut()
        } else if shown, panel.alphaValue != alpha {
            animate(duration: 0.2) { self.panel.animator().alphaValue = alpha }
        }
    }

    private func fadeIn(to alpha: CGFloat) {
        panel.alphaValue = 0
        panel.orderFrontRegardless() // shows without activating the app
        animate(duration: 0.25) { self.panel.animator().alphaValue = alpha }
        DispatchQueue.main.async { self.panel.invalidateShadow() }
    }

    private func fadeOut() {
        animate(duration: 0.25) {
            self.panel.animator().alphaValue = 0
        } completion: {
            if !self.shown { self.panel.orderOut(nil) }
        }
    }

    private func resize(to size: CGSize) {
        let visible = Self.visibleFrame(containing: panel.frame)
        let target = SnapGeometry.resize(panel.frame, to: size, in: visible)
        // The same corner SnapGeometry.resize holds still (screen y runs up).
        let anchor = Alignment(horizontal: panel.frame.midX > visible.midX ? .trailing : .leading,
                               vertical: panel.frame.midY > visible.midY ? .top : .bottom)
        if settings.morphAnchor != anchor { settings.morphAnchor = anchor }
        move(to: target, morph: true)
    }

    // MARK: Drag and snap

    private func dragChanged() {
        let mouse = NSEvent.mouseLocation
        if dragStart == nil { dragStart = (panel.frame.origin, mouse) }
        guard let start = dragStart else { return }
        panel.setFrameOrigin(CGPoint(x: start.origin.x + mouse.x - start.mouse.x, y: start.origin.y + mouse.y - start.mouse.y))
    }

    private func dragEnded() {
        dragStart = nil
        let target = SnapGeometry.settle(panel.frame, in: Self.visibleFrame(containing: panel.frame))
        move(to: target, morph: false)
    }

    private func resizeBegan() {
        resizeStart = (panel.frame, NSEvent.mouseLocation)
    }

    private func resizeChanged(_ edges: ResizeEdges) {
        let mouse = NSEvent.mouseLocation
        guard let start = resizeStart else { return }
        let delta = CGVector(dx: mouse.x - start.mouse.x, dy: mouse.y - start.mouse.y)
        let space = model.lyricsSpace
        let vertical = edges.contains(.top) || edges.contains(.bottom)
        let maxSize = CGSize(width: OverlayLayout.maxSize.width, height: OverlayLayout.maxDrawnHeight(lyricsSpace: space))
        let frame = SnapGeometry.resized(start.frame, edges: edges, by: delta, min: OverlayLayout.minSize, max: vertical ? maxSize : OverlayLayout.maxSize)
        panel.setFrame(frame, display: true)
        // The height picks the layout, live: crossing a threshold swaps it under the
        // pointer. A sideways-only drag leaves the stored height alone, so a folded
        // panel stays folded.
        settings.setOverlaySize(vertical
            ? OverlayLayout.userSize(drawn: frame.size, lyricsSpace: space)
            : CGSize(width: frame.width, height: settings.overlaySize.height))
    }

    private func resizeEnded() {
        resizeStart = nil
        // Keep it where the user put it: only pull it back on screen. Snapping
        // here would undo a resize of an edge that sits against the screen edge.
        let area = Self.visibleFrame(containing: panel.frame).insetBy(dx: SnapGeometry.margin, dy: SnapGeometry.margin)
        move(to: SnapGeometry.clamp(panel.frame, in: area), morph: false)
    }

    private func screensChanged() {
        let target = SnapGeometry.settle(panel.frame, in: Self.visibleFrame(containing: panel.frame))
        move(to: target, morph: false)
    }

    private func move(to frame: CGRect, morph: Bool) {
        settings.overlayFrame = frame
        let reduce = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        guard shown, !reduce || morph else {
            panel.setFrame(frame, display: true)
            panel.invalidateShadow()
            return
        }
        // spring.float (0.55 s) / spring.morph (0.42 s), as a springy curve.
        // spring.resize (0.34 s, nearly critically damped) for size changes, as on Windows.
        let duration = reduce ? 0.15 : (morph ? 0.34 : 0.5)
        animate(duration: duration, timing: reduce ? CAMediaTimingFunction(name: .easeOut) : CAMediaTimingFunction(controlPoints: 0.2, 0.9, 0.3, 1)) {
            self.panel.animator().setFrame(frame, display: true)
        } completion: {
            self.panel.invalidateShadow()
        }
    }

    private func animate(
        duration: TimeInterval, timing: CAMediaTimingFunction = CAMediaTimingFunction(name: .easeInEaseOut),
        _ changes: @escaping () -> Void, completion: (@MainActor @Sendable () -> Void)? = nil
    ) {
        let handler: (@Sendable () -> Void)? = completion.map { done in { @Sendable in MainActor.assumeIsolated(done) } }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = duration
            ctx.timingFunction = timing
            ctx.allowsImplicitAnimation = true
            changes()
        }, completionHandler: handler)
    }

    /// Visible frame (minus menu bar and Dock) of the screen holding most of `frame`.
    static func visibleFrame(containing frame: CGRect?) -> CGRect {
        let screens = NSScreen.screens
        if let frame, let best = screens.max(by: { overlap($0.frame, frame) < overlap($1.frame, frame) }), overlap(best.frame, frame) > 0 {
            return best.visibleFrame
        }
        if let frame, let nearest = screens.min(by: { distance($0.frame, frame) < distance($1.frame, frame) }) {
            return nearest.visibleFrame
        }
        return (NSScreen.main ?? screens.first)?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1440, height: 900)
    }

    private static func overlap(_ a: CGRect, _ b: CGRect) -> CGFloat {
        let i = a.intersection(b)
        return i.isNull ? 0 : i.width * i.height
    }

    private static func distance(_ a: CGRect, _ b: CGRect) -> CGFloat {
        hypot(a.midX - b.midX, a.midY - b.midY)
    }
}
