import CoreImage
import CryptoKit
import Foundation
import ImageIO
import OvertoneCore

/// Everything derived from one cover, computed once per artwork off the main thread.
struct ProcessedArtwork: @unchecked Sendable {
    /// For display (at most 256 px, enough for a 64 pt cover at 3×).
    let image: CGImage
    /// Pre-blurred, low-resolution backdrop: blurred once per track, never live.
    let backdrop: CGImage?
    /// 64×64 straight RGBA, kept so a tone change can re-run the palette without decoding.
    let sample: Data
    let palette: OverlayPalette
}

enum ArtworkProcessor {
    static let sampleSize = 64

    static func digest(_ data: Data) -> String {
        SHA256.hash(data: data).prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    static func process(_ data: Data, tone: Tone?) -> ProcessedArtwork? {
        guard let src = CGImageSourceCreateWithData(data as CFData, nil),
              let image = thumbnail(src, maxPixels: 256),
              let sample = rgba(image, size: sampleSize)
        else { return nil }
        let palette = paletteFromRgba(rgba: sample, width: UInt32(sampleSize), height: UInt32(sampleSize), forceTone: tone)
        return ProcessedArtwork(image: image, backdrop: blurred(image), sample: sample, palette: palette)
    }

    static func palette(sample: Data, tone: Tone?) -> OverlayPalette {
        paletteFromRgba(rgba: sample, width: UInt32(sampleSize), height: UInt32(sampleSize), forceTone: tone)
    }

    private static func thumbnail(_ src: CGImageSource, maxPixels: Int) -> CGImage? {
        let opts: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        return CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary)
    }

    /// Straight (un-premultiplied) sRGB RGBA at `size`×`size`.
    static func rgba(_ image: CGImage, size: Int) -> Data? {
        var bytes = [UInt8](repeating: 0, count: size * size * 4)
        let ok = bytes.withUnsafeMutableBytes { buf -> Bool in
            guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let ctx = CGContext(
                      data: buf.baseAddress, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4,
                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                  )
            else { return false }
            ctx.interpolationQuality = .medium
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: size, height: size))
            return true
        }
        guard ok else { return nil }
        // Un-premultiply so semi-transparent edges keep their real colour.
        for i in stride(from: 0, to: bytes.count, by: 4) {
            let a = bytes[i + 3]
            if a > 0, a < 255 {
                for c in 0..<3 { bytes[i + c] = UInt8(min(255, Int(bytes[i + c]) * 255 / Int(a))) }
            }
        }
        return Data(bytes)
    }

    private static let ciContext = CIContext(options: [.cacheIntermediates: false])

    /// The cover's own colours, blurred and slightly saturated (concept: blur 26px, saturate 1.3).
    private static func blurred(_ image: CGImage) -> CGImage? {
        let small = 48.0
        let scale = small / Double(max(image.width, image.height))
        let input = CIImage(cgImage: image)
            .transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            .clampedToExtent()
        guard let blur = CIFilter(name: "CIGaussianBlur", parameters: [kCIInputImageKey: input, kCIInputRadiusKey: 4]),
              let blurredImage = blur.outputImage,
              let sat = CIFilter(name: "CIColorControls", parameters: [kCIInputImageKey: blurredImage, kCIInputSaturationKey: 1.3]),
              let out = sat.outputImage
        else { return nil }
        let extent = CGRect(x: 0, y: 0, width: small, height: small)
        return ciContext.createCGImage(out.cropped(to: extent), from: extent)
    }
}
