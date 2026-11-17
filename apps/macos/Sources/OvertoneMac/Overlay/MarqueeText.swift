import AppKit
import QuartzCore
import SwiftUI

/// One line of text that never truncates: when it doesn't fit, it scrolls.
/// Holds at the start, glides to the end at a reading pace, holds, glides back,
/// with edges fading on the side where text is hidden. The scroll is one
/// repeating Core Animation, so the window server runs it and the app does no
/// work per frame (a SwiftUI timeline here cost ~8% CPU). Static when it fits.
struct MarqueeText: NSViewRepresentable {
    let text: String
    var size: CGFloat
    var weight: NSFont.Weight = .regular
    var kerning: CGFloat = 0
    var color: Color
    /// Set for a lyric line: instead of looping, a line too wide scrolls across
    /// once, timed to land on its end before the next line (the Windows pill).
    var glide: Glide?

    struct Glide: Equatable {
        /// Wall time until the next lyric line; nil when paused or unknown.
        var msToNext: UInt64?
        /// Reduce Motion: no movement, an ellipsis instead (as on Windows).
        var reduceMotion = false

        static let startDelay: TimeInterval = 0.9
        static let endHold: TimeInterval = 0.7
        /// Reading pace and the fastest it may go to finish before the next line (pt/s).
        static let comfortable: CGFloat = 45
        static let fastest: CGFloat = 140
        static let edge: CGFloat = 18

        /// Glide duration for `overflow` points (already including the edge fade).
        static func duration(overflow: CGFloat, msToNext: UInt64?) -> TimeInterval {
            var seconds = TimeInterval(overflow / comfortable)
            if let left = msToNext {
                let room = (TimeInterval(left) / 1000) - startDelay - endHold
                seconds = max(TimeInterval(overflow / fastest), min(seconds, room))
            }
            return max(0.3, seconds)
        }
    }

    static let hold: TimeInterval = 1.8
    static let back: TimeInterval = 0.6
    static let speed: CGFloat = 32
    static let fade: CGFloat = 14

    func makeNSView(context: Context) -> MarqueeView { MarqueeView() }

    func updateNSView(_ view: MarqueeView, context: Context) {
        view.configure(text: text, font: .systemFont(ofSize: size, weight: weight), kerning: kerning, color: NSColor(color), glide: glide)
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: MarqueeView, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? nsView.textSize.width, height: nsView.textSize.height)
    }

    /// Scroll position (0...overflow) at `t` seconds into the cycle.
    static func offset(at t: TimeInterval, overflow: CGFloat, speed: CGFloat = speed) -> CGFloat {
        let scroll = TimeInterval(overflow / speed)
        let period = hold + scroll + hold + back
        var p = t.truncatingRemainder(dividingBy: period)
        if p < hold { return 0 }
        p -= hold
        if p < scroll { return overflow * CGFloat(p / scroll) }
        p -= scroll
        if p < hold { return overflow }
        p -= hold
        let e = p / back
        return overflow * CGFloat(1 - e * e * (3 - 2 * e))
    }
}

final class MarqueeView: NSView {
    /// Owned clip container: AppKit manages the backing layer and may reset its
    /// mask/clipping, so the scroll and fades live one level down.
    private let clipLayer = CALayer()
    private let textLayer = CATextLayer()
    private let fadeMask = CAGradientLayer()
    private var text = ""
    private var font = NSFont.systemFont(ofSize: 13)
    private var kerning: CGFloat = 0
    private var color = NSColor.white
    private(set) var textSize = CGSize.zero
    /// What the running animation was built for, to avoid restarting it needlessly.
    private var installed: (text: String, overflow: CGFloat)?
    private var glide: MarqueeText.Glide?
    /// When this line appeared: a one-off glide is timed from it, so re-installing
    /// (Core Animation drops animations when the view changes window) resumes in place.
    private var glideStart = CACurrentMediaTime()

    init() {
        super.init(frame: .zero)
        wantsLayer = true
        layer?.addSublayer(clipLayer)
        clipLayer.masksToBounds = true
        clipLayer.anchorPoint = .zero
        clipLayer.addSublayer(textLayer)
        textLayer.anchorPoint = .zero
        textLayer.isWrapped = false
        fadeMask.startPoint = CGPoint(x: 0, y: 0.5)
        fadeMask.endPoint = CGPoint(x: 1, y: 0.5)
        setAccessibilityElement(true)
        setAccessibilityRole(.staticText)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }

    func configure(text: String, font: NSFont, kerning: CGFloat, color: NSColor, glide: MarqueeText.Glide? = nil) {
        if glide?.reduceMotion != self.glide?.reduceMotion || (glide == nil) != (self.glide == nil) { installed = nil }
        if text != self.text { glideStart = CACurrentMediaTime() }
        self.glide = glide
        let changed = text != self.text || font != self.font || kerning != self.kerning
        let recolored = color != self.color
        guard changed || recolored else { return }
        self.text = text
        self.font = font
        self.kerning = kerning
        self.color = color
        let attributed = NSAttributedString(string: text, attributes: [.font: font, .kern: kerning, .foregroundColor: color])
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        textLayer.string = attributed
        if changed {
            let s = attributed.size()
            textSize = CGSize(width: ceil(s.width) + 1, height: ceil(s.height))
            textLayer.bounds = CGRect(origin: .zero, size: textSize)
            invalidateIntrinsicContentSize()
            setAccessibilityLabel(text)
        }
        CATransaction.commit()
        if changed { install() }
    }

    override func layout() {
        super.layout()
        install()
    }

    // SwiftUI resizes representables by setting the frame, which doesn't always trigger layout().
    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        install()
    }

    private var occlusionObserver: NSObjectProtocol?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // Core Animation drops animations when a layer leaves the window, and
        // may stop them while the window is occluded; reinstall on both.
        if let occlusionObserver { NotificationCenter.default.removeObserver(occlusionObserver) }
        occlusionObserver = window.map { w in
            NotificationCenter.default.addObserver(forName: NSWindow.didChangeOcclusionStateNotification, object: w, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, self.window?.occlusionState.contains(.visible) == true else { return }
                    self.installed = nil
                    self.install()
                }
            }
        }
        installed = nil
        install()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        textLayer.contentsScale = window?.backingScaleFactor ?? 2
    }

    private func install() {
        textLayer.contentsScale = window?.backingScaleFactor ?? 2
        let overflow = max(0, textSize.width - bounds.width)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        clipLayer.frame = bounds
        textLayer.position = CGPoint(x: 0, y: (bounds.height - textSize.height) / 2)
        textLayer.bounds = CGRect(origin: .zero, size: textSize)
        textLayer.truncationMode = .none
        fadeMask.frame = CGRect(origin: .zero, size: bounds.size)
        guard overflow > 0.5 else {
            textLayer.removeAllAnimations()
            fadeMask.removeAllAnimations()
            clipLayer.mask = nil
            installed = nil
            return
        }
        if let installed, installed.text == text, abs(installed.overflow - overflow) < 0.5,
           textLayer.animation(forKey: "marquee") != nil { return }
        installed = (text, overflow)
        if let glide {
            installGlide(overflow: overflow, glide)
            return
        }

        let hold = MarqueeText.hold, back = MarqueeText.back
        let scroll = TimeInterval(overflow / MarqueeText.speed)
        let period = hold + scroll + hold + back
        let fadeTime = min(TimeInterval(MarqueeText.fade / MarqueeText.speed), scroll / 2)
        func k(_ t: TimeInterval) -> NSNumber { NSNumber(value: t / period) }

        let move = CAKeyframeAnimation(keyPath: "position.x")
        move.values = [0, 0, -overflow, -overflow, 0]
        move.keyTimes = [0, k(hold), k(hold + scroll), k(hold + scroll + hold), 1]
        move.timingFunctions = [.init(name: .linear), .init(name: .linear), .init(name: .linear), .init(name: .easeInEaseOut)]
        move.duration = period
        move.repeatCount = .infinity
        textLayer.add(move, forKey: "marquee")

        // Edge fades follow the scroll: right edge faded at the start, both while
        // moving, left edge faded at the end.
        let w = max(bounds.width, 1), f = min(MarqueeText.fade / w, 0.25)
        fadeMask.locations = [0, NSNumber(value: f), NSNumber(value: 1 - f), 1]
        let solid = NSColor.black.cgColor, clear = NSColor.clear.cgColor
        let atStart = [solid, solid, solid, clear], moving = [clear, solid, solid, clear], atEnd = [clear, solid, solid, solid]
        fadeMask.colors = atStart
        let fades = CAKeyframeAnimation(keyPath: "colors")
        fades.values = [atStart, atStart, moving, moving, atEnd, atEnd, moving, atStart]
        fades.keyTimes = [0, k(hold), k(hold + fadeTime), k(hold + scroll - fadeTime), k(hold + scroll),
                          k(hold + scroll + hold), k(hold + scroll + hold + back / 2), 1]
        fades.duration = period
        fades.repeatCount = .infinity
        fadeMask.add(fades, forKey: "marquee")
        clipLayer.mask = fadeMask
    }

    /// One pass, as the Windows pill does: a beat to start reading, then an even
    /// glide that lands on the line's end before the next line is due, and stays there.
    private func installGlide(overflow: CGFloat, _ glide: MarqueeText.Glide) {
        textLayer.removeAllAnimations()
        fadeMask.removeAllAnimations()
        if glide.reduceMotion {
            // No motion: fall back to an ellipsis.
            textLayer.bounds = CGRect(x: 0, y: 0, width: bounds.width, height: textSize.height)
            textLayer.truncationMode = .end
            clipLayer.mask = nil
            return
        }
        // The glide carries the end clear of the right edge's fade.
        let distance = overflow + MarqueeText.Glide.edge
        let duration = MarqueeText.Glide.duration(overflow: distance, msToNext: glide.msToNext)

        let move = CABasicAnimation(keyPath: "position.x")
        move.fromValue = 0
        move.toValue = -distance
        move.beginTime = textLayer.convertTime(glideStart + MarqueeText.Glide.startDelay, from: nil)
        move.duration = duration
        move.timingFunction = CAMediaTimingFunction(name: .linear)
        move.fillMode = .both
        move.isRemovedOnCompletion = false
        textLayer.add(move, forKey: "marquee")

        // Soft edges instead of a hard cut: the right fades while text runs past it,
        // the left once the line moves.
        let w = max(bounds.width, 1), f = min(MarqueeText.Glide.edge / w, 0.25)
        fadeMask.locations = [0, NSNumber(value: f), NSNumber(value: 1 - f), 1]
        let solid = NSColor.black.cgColor, clear = NSColor.clear.cgColor
        fadeMask.colors = [solid, solid, solid, clear]
        let left = CABasicAnimation(keyPath: "colors")
        left.fromValue = [solid, solid, solid, clear]
        left.toValue = [clear, solid, solid, clear]
        left.beginTime = fadeMask.convertTime(glideStart + MarqueeText.Glide.startDelay, from: nil)
        left.duration = 0.25
        left.fillMode = .both
        left.isRemovedOnCompletion = false
        fadeMask.add(left, forKey: "marquee")
        clipLayer.mask = fadeMask
    }
}
