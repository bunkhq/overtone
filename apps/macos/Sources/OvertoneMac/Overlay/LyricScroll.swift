import AppKit
import Observation
import SwiftUI

/// Wheel and trackpad scrolling for the lyric list. A local event monitor
/// catches scroll events over the list (the overlay never becomes key, but the
/// window under the pointer still gets the wheel). Scrolling detaches the list
/// from the playing line; after a pause it glides back, as on Windows.
@MainActor
@Observable
final class LyricScroll {
    /// Where the user scrolled to; `nil` while following the song.
    private(set) var userOffset: CGFloat?

    @ObservationIgnored var followOffset: CGFloat = 0
    @ObservationIgnored var maxOffset: CGFloat = 0
    /// The list's frame in the window (SwiftUI global space: top-left origin).
    @ObservationIgnored var frame: CGRect = .zero
    @ObservationIgnored private var monitor: Any?
    @ObservationIgnored private var resume: Task<Void, Never>?

    static let resumeAfter: Duration = .milliseconds(2500)
    /// A mouse-wheel notch (non-precise deltas count lines).
    static let pointsPerLine: CGFloat = 22

    func start() {
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
            let consumed = MainActor.assumeIsolated { self?.handle(event) ?? false }
            return consumed ? nil : event
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        resume?.cancel()
        userOffset = nil
    }

    /// Back to following the playing line now (after a seek).
    func follow() {
        resume?.cancel()
        userOffset = nil
    }

    /// Returns true when the event scrolled the lyrics (and is consumed).
    private func handle(_ event: NSEvent) -> Bool {
        guard let window = event.window, window is OverlayPanel, maxOffset > 0.5,
              let height = window.contentView?.bounds.height else { return false }
        let p = event.locationInWindow
        guard frame.contains(CGPoint(x: p.x, y: height - p.y)) else { return false }
        let dy = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY : event.scrollingDeltaY * Self.pointsPerLine
        guard dy != 0 else { return true }
        userOffset = min(max((userOffset ?? followOffset) - dy, 0), maxOffset)
        resume?.cancel()
        resume = Task { [weak self] in
            try? await Task.sleep(for: Self.resumeAfter)
            guard !Task.isCancelled, let self else { return }
            withAnimation(.easeInOut(duration: 0.52)) { self.userOffset = nil }
        }
        return true
    }
}
