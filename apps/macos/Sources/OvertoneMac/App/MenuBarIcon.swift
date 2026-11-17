import AppKit

/// The menu bar glyph: the icon's three standing waves (modes 1–3 of a string),
/// drawn from the same geometry as scripts/gen-icon.mjs. By default a template
/// image, so macOS tints it for light, dark and highlighted menu bars; optionally
/// drawn in the track's colours.
enum MenuBarIcon {
    /// `colors`: three wave colours for the coloured icon (top to bottom); nil for the template.
    static func make(pointSize: CGFloat = 18, colors: [NSColor]? = nil) -> NSImage {
        let image = NSImage(size: NSSize(width: pointSize, height: pointSize), flipped: true) { rect in
            // Source grid: 1024 units, cropped to the mark's bounds (132...892).
            let crop: CGFloat = 760, origin: CGFloat = 132
            let s = rect.width / crop
            let waves: [(n: Double, yc: CGFloat, amp: CGFloat, alpha: CGFloat)] = [
                (1, 326, 80, 1), (2, 526, 64, 0.72), (3, 726, 52, 0.48),
            ]
            for (i, w) in waves.enumerated() {
                let path = NSBezierPath()
                for step in 0...60 {
                    let t = Double(step) / 60
                    let x = (176 + 672 * CGFloat(t) - origin) * s
                    let y = (w.yc - w.amp * CGFloat(sin(w.n * .pi * t)) - origin) * s
                    step == 0 ? path.move(to: NSPoint(x: x, y: y)) : path.line(to: NSPoint(x: x, y: y))
                }
                path.lineWidth = 92 * s
                path.lineCapStyle = .round
                path.lineJoinStyle = .round
                (colors.map { $0[i % $0.count] } ?? NSColor.black.withAlphaComponent(w.alpha)).setStroke()
                path.stroke()
            }
            return true
        }
        image.isTemplate = colors == nil
        image.accessibilityDescription = "Overtone"
        return image
    }
}
