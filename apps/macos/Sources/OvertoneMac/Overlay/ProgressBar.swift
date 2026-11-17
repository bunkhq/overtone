import AppKit
import QuartzCore
import SwiftUI

/// A progress bar the window server animates. On each play, pause or seek it
/// starts one linear Core Animation from the current fraction to 1 over the
/// remaining time, then costs the app nothing per frame.
struct ProgressBar: NSViewRepresentable {
    var state: ProgressState?
    var track: Color
    var fill: Color
    /// While the user scrubs: the fraction under the pointer, shown instead of playback.
    var scrub: Double?

    func makeNSView(context: Context) -> ProgressBarView { ProgressBarView() }

    func updateNSView(_ view: ProgressBarView, context: Context) {
        view.setColors(track: NSColor(track), fill: NSColor(fill))
        view.apply(state)
        view.setScrub(scrub)
    }
}

/// The progress bar as a time slider, ported from the Windows overlay: hover
/// thickens it, press or drag scrubs (the fill, and the elapsed time where
/// shown, follow the pointer), release seeks. A 12 pt hit strip around the thin
/// bar makes it easy to grab without changing the layout, and pressing it
/// doesn't drag the window.
struct ScrubBar: View {
    var state: ProgressState?
    var track: Color
    var fill: Color
    var height: CGFloat
    var hoverScale: CGFloat
    var durationMs: UInt64?
    /// Nil when the player can't seek: the bar stays a plain indicator.
    var seek: ((UInt64) -> Void)?
    @Binding var scrub: Double?

    @State private var hovering = false
    @State private var width: CGFloat = 0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let hitHeight: CGFloat = 12

    private var seekable: Bool { seek != nil && (durationMs ?? 0) > 0 }

    static func fraction(x: CGFloat, width: CGFloat) -> Double {
        width > 0 ? Double(min(max(x / width, 0), 1)) : 0
    }

    var body: some View {
        let active = seekable && (hovering || scrub != nil)
        ProgressBar(state: state, track: track, fill: fill, scrub: scrub)
            .frame(height: height)
            .scaleEffect(x: 1, y: active ? hoverScale : 1)
            .animation(reduceMotion ? nil : .easeOut(duration: 0.14), value: active)
            .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
            .overlay {
                Color.clear
                    .frame(height: Self.hitHeight)
                    .contentShape(Rectangle())
                    .onHover { inside in
                        hovering = inside
                        if scrub == nil { (inside && seekable ? NSCursor.pointingHand : NSCursor.arrow).set() }
                    }
                    .gesture(
                        DragGesture(minimumDistance: 0, coordinateSpace: .local)
                            .onChanged { v in
                                guard seekable else { return }
                                scrub = Self.fraction(x: v.location.x, width: width)
                            }
                            .onEnded { v in
                                defer { scrub = nil }
                                guard seekable, let seek, let d = durationMs else { return }
                                seek(UInt64(Self.fraction(x: v.location.x, width: width) * Double(d)))
                            }
                    )
            }
            .accessibilityElement()
            .accessibilityLabel("Playback position")
            .accessibilityValue(formatTime(UInt64((state?.fraction ?? 0) * Double(durationMs ?? 0))))
            .accessibilityAdjustableAction { direction in
                guard seekable, let seek, let d = durationMs else { return }
                let now = Int64((state?.fraction ?? 0) * Double(d)), step: Int64 = 10_000
                seek(UInt64(max(0, min(Int64(d), now + (direction == .increment ? step : -step)))))
            }
    }
}

final class ProgressBarView: NSView {
    private let trackLayer = CALayer()
    private let fillLayer = CALayer()
    private var epoch: Int?
    private static let key = "progress"

    init() {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.addSublayer(trackLayer)
        trackLayer.masksToBounds = true
        trackLayer.addSublayer(fillLayer)
        fillLayer.anchorPoint = CGPoint(x: 0, y: 0.5)
        fillLayer.transform = CATransform3DMakeScale(0, 1, 1)
        setAccessibilityRole(.progressIndicator)
        setAccessibilityLabel("Playback progress")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        trackLayer.frame = bounds
        trackLayer.cornerRadius = bounds.height / 2
        fillLayer.bounds = CGRect(origin: .zero, size: bounds.size)
        fillLayer.position = CGPoint(x: 0, y: bounds.midY)
        fillLayer.cornerRadius = bounds.height / 2
        CATransaction.commit()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        let scale = window?.backingScaleFactor ?? 2
        trackLayer.contentsScale = scale
        fillLayer.contentsScale = scale
    }

    func setColors(track: NSColor, fill: NSColor) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        trackLayer.backgroundColor = track.cgColor
        fillLayer.backgroundColor = fill.cgColor
        CATransaction.commit()
    }

    /// The running animation's starting point, kept so it can be re-installed:
    /// Core Animation drops animations when a layer leaves the window's tree
    /// (SwiftUI inserts the view after its first update, layouts swap, etc.).
    private var anchor: (fraction: Double, remaining: TimeInterval?, at: CFTimeInterval)?

    private var scrubbing = false

    /// Shows a fixed fraction while the user scrubs; nil hands back to playback.
    func setScrub(_ fraction: Double?) {
        if let fraction {
            scrubbing = true
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            fillLayer.removeAnimation(forKey: Self.key)
            fillLayer.transform = CATransform3DMakeScale(fraction, 1, 1)
            CATransaction.commit()
        } else if scrubbing {
            scrubbing = false
            install()
        }
    }

    func apply(_ state: ProgressState?) {
        guard state?.epoch != epoch else { return }
        epoch = state?.epoch
        anchor = state.map { ($0.fraction, $0.remaining, CACurrentMediaTime()) }
        install()
    }

    private var occlusionObserver: NSObjectProtocol?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
        occlusionObserver = window.map { w in
            NotificationCenter.default.addObserver(forName: NSWindow.didChangeOcclusionStateNotification, object: w, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    if self?.window?.occlusionState.contains(.visible) == true { self?.install() }
                }
            }
        }
        install()
    }

    private func install() {
        guard !scrubbing else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        fillLayer.removeAnimation(forKey: Self.key)
        guard let anchor else {
            fillLayer.transform = CATransform3DMakeScale(0, 1, 1)
            return
        }
        var from = anchor.fraction
        var remaining = anchor.remaining ?? 0
        if remaining > 0 {
            let elapsed = CACurrentMediaTime() - anchor.at
            from = min(1, from + (1 - from) * elapsed / remaining)
            remaining -= elapsed
        }
        setAccessibilityValue(NSNumber(value: from))
        guard remaining > 0.05, from < 1 else {
            fillLayer.transform = CATransform3DMakeScale(from, 1, 1)
            return
        }
        fillLayer.transform = CATransform3DMakeScale(1, 1, 1)
        let anim = CABasicAnimation(keyPath: "transform.scale.x")
        anim.fromValue = from
        anim.toValue = 1
        anim.duration = remaining
        anim.timingFunction = CAMediaTimingFunction(name: .linear)
        fillLayer.add(anim, forKey: Self.key)
    }
}
