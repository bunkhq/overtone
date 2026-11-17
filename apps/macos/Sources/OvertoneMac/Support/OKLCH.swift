import AppKit
import OvertoneCore
import SwiftUI

/// An OKLCH colour. The core picks palettes; the shell only needs OKLCH to
/// cross-fade between them without the muddy midpoint an RGB blend gives.
/// `h` is in degrees and may be "unwrapped" (outside 0..<360) so a linear
/// interpolation always takes the short way round the hue circle.
public struct OKLCH: Equatable, Sendable {
    public var l: Double
    public var c: Double
    public var h: Double

    public init(l: Double, c: Double, h: Double) {
        self.l = l
        self.c = c
        self.h = h
    }

    public init(rgb: Rgb) {
        func linear(_ v: UInt8) -> Double {
            let s = Double(v) / 255
            return s <= 0.04045 ? s / 12.92 : pow((s + 0.055) / 1.055, 2.4)
        }
        let (r, g, b) = (linear(rgb.r), linear(rgb.g), linear(rgb.b))
        let l_ = cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
        let m_ = cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
        let s_ = cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
        let L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_
        let A = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_
        let B = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
        var hue = atan2(B, A) * 180 / .pi
        if hue < 0 { hue += 360 }
        self.init(l: L, c: (A * A + B * B).squareRoot(), h: hue)
    }

    /// sRGB components 0...1, clamped into gamut.
    public var srgb: (r: Double, g: Double, b: Double) {
        let hr = h * .pi / 180
        let A = c * cos(hr), B = c * sin(hr)
        let l_ = l + 0.3963377774 * A + 0.2158037573 * B
        let m_ = l - 0.1055613458 * A - 0.0638541728 * B
        let s_ = l - 0.0894841775 * A - 1.2914855480 * B
        let (L, M, S) = (l_ * l_ * l_, m_ * m_ * m_, s_ * s_ * s_)
        func gamma(_ x: Double) -> Double {
            let v = x <= 0.0031308 ? 12.92 * x : 1.055 * pow(max(x, 0), 1 / 2.4) - 0.055
            return min(max(v, 0), 1)
        }
        return (
            gamma(4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S),
            gamma(-1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S),
            gamma(-0.0041960863 * L - 0.7034186147 * M + 1.7076147010 * S)
        )
    }

    public var color: Color {
        let c = srgb
        return Color(.sRGB, red: c.r, green: c.g, blue: c.b)
    }

    public var nsColor: NSColor {
        let c = srgb
        return NSColor(srgbRed: c.r, green: c.g, blue: c.b, alpha: 1)
    }

    /// This colour with its hue moved by whole turns to sit nearest `reference`.
    /// Near-grey colours have no meaningful hue, so they borrow the reference's.
    func unwrapped(near reference: OKLCH) -> OKLCH {
        var out = self
        if c < 0.012 {
            out.h = reference.h
            return out
        }
        let delta = (h - reference.h).truncatingRemainder(dividingBy: 360)
        let shortest = delta > 180 ? delta - 360 : (delta < -180 ? delta + 360 : delta)
        out.h = reference.h + shortest
        return out
    }
}

/// The overlay's resolved colours, as SwiftUI reads them from the environment.
public struct OverlayColors: Equatable, Sendable {
    public var tone: Tone
    public var base: OKLCH
    public var accent: OKLCH
    public var ink: OKLCH
    public var inkSoft: OKLCH
    public var inkFaint: OKLCH
    /// The gradient background's top-left corner (base tinted toward accent, luminance held).
    public var corner: OKLCH

    public init(_ p: OverlayPalette, near previous: OverlayColors? = nil) {
        tone = p.tone
        base = OKLCH(rgb: p.base)
        accent = OKLCH(rgb: p.accent)
        ink = OKLCH(rgb: p.ink)
        inkSoft = OKLCH(rgb: p.inkSoft)
        inkFaint = OKLCH(rgb: p.inkFaint)
        corner = OKLCH(rgb: p.corner)
        if let previous {
            base = base.unwrapped(near: previous.base)
            accent = accent.unwrapped(near: previous.accent)
            ink = ink.unwrapped(near: previous.ink)
            inkSoft = inkSoft.unwrapped(near: previous.inkSoft)
            inkFaint = inkFaint.unwrapped(near: previous.inkFaint)
            corner = corner.unwrapped(near: previous.corner)
        }
    }

    init(vector v: PaletteVector, tone: Tone) {
        self.tone = tone
        let s = v.storage
        base = OKLCH(l: s[0], c: s[1], h: s[2])
        accent = OKLCH(l: s[3], c: s[4], h: s[5])
        ink = OKLCH(l: s[6], c: s[7], h: s[8])
        inkSoft = OKLCH(l: s[9], c: s[10], h: s[11])
        inkFaint = OKLCH(l: s[12], c: s[13], h: s[14])
        corner = OKLCH(l: s[15], c: s[16], h: s[17])
    }

    var vector: PaletteVector {
        let all = [base, accent, ink, inkSoft, inkFaint, corner]
        var s = SIMD32<Double>()
        for (i, c) in all.enumerated() {
            s[i * 3] = c.l
            s[i * 3 + 1] = c.c
            s[i * 3 + 2] = c.h
        }
        return PaletteVector(storage: s)
    }

    public static let neutral = OverlayColors(neutralPalette())
}

/// Six OKLCH colours flattened for SwiftUI's animation system, which then
/// interpolates every channel linearly: a cross-fade in OKLCH.
struct PaletteVector: VectorArithmetic, Sendable {
    var storage: SIMD32<Double>

    static var zero: PaletteVector { PaletteVector(storage: .zero) }
    static func + (a: PaletteVector, b: PaletteVector) -> PaletteVector { PaletteVector(storage: a.storage + b.storage) }
    static func - (a: PaletteVector, b: PaletteVector) -> PaletteVector { PaletteVector(storage: a.storage - b.storage) }
    mutating func scale(by rhs: Double) { storage *= rhs }
    var magnitudeSquared: Double { (storage * storage).sum() }
}

/// Publishes animated palette colours to the views below it.
struct PaletteEnvironment: ViewModifier, Animatable {
    var vector: PaletteVector
    var tone: Tone

    init(_ colors: OverlayColors) {
        vector = colors.vector
        tone = colors.tone
    }

    nonisolated var animatableData: PaletteVector {
        get { vector }
        set { vector = newValue }
    }

    func body(content: Content) -> some View {
        content.environment(\.overlayColors, OverlayColors(vector: vector, tone: tone))
    }
}

extension EnvironmentValues {
    @Entry var overlayColors: OverlayColors = .neutral
}

extension OverlayColors {
    /// Colours for Liquid Glass. The palette's inks are solved against its solid
    /// base colour, but glass lets the desktop through and sets its own light or
    /// dark appearance, so on glass the text follows that appearance the way
    /// system labels do (label, secondary and tertiary label, faintly tinted
    /// toward the track's hue). The track's colour stays in the glass tint, kept
    /// at the glass's own lightness so it never fights the text, and in the
    /// highlight, whose lightness is set to read on that glass.
    /// How much of the glass a layer of `base` covers. Glass alone goes mid-grey over a
    /// contrasting desktop (white behind dark glass, black behind light glass), which
    /// no text colour survives. Measured on screen over white, near-black and yellow
    /// desktops, these keep the title at 7:1 or better, the artist at 4.5:1 and dimmed
    /// lyric lines at 3:1 in every case. The rest stays glass: its edge, light and blur.
    static func glassScrim(dark: Bool) -> Double { dark ? 0.66 : 0.5 }

    func onGlass(dark: Bool) -> OverlayColors {
        var c = self
        let h = base.h
        c.tone = dark ? .dark : .light
        if dark {
            c.ink = OKLCH(l: 0.985, c: 0.004, h: h)
            c.inkSoft = OKLCH(l: 0.86, c: 0.012, h: h)
            c.inkFaint = OKLCH(l: 0.70, c: 0.014, h: h)
        } else {
            c.ink = OKLCH(l: 0.16, c: 0.01, h: h)
            c.inkSoft = OKLCH(l: 0.36, c: 0.014, h: h)
            c.inkFaint = OKLCH(l: 0.44, c: 0.016, h: h)
        }
        c.accent = OKLCH(l: dark ? 0.86 : 0.45, c: min(max(accent.c, 0.06), 0.16), h: accent.h)
        c.base = OKLCH(l: dark ? 0.24 : 0.95, c: min(base.c, 0.05), h: h)
        c.corner = c.base
        return c
    }
}
