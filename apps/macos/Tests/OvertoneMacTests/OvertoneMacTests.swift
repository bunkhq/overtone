import Foundation
import OvertoneCore
import Testing
@testable import OvertoneMac

@Suite struct AdapterStreamTests {
    private func line(_ json: String) -> Data { Data((json + "\n").utf8) }

    @Test func fullThenDiffPayloadsMerge() throws {
        let p = AdapterStreamParser()
        let full = p.feed(line(#"{"type":"data","diff":false,"payload":{"bundleIdentifier":"com.spotify.client","title":"Song","artist":"Artist","album":"LP","playing":true,"playbackRate":1,"durationMicros":200000000,"elapsedTimeMicros":12500000,"timestampEpochMicros":1763200000000000}}"#))
        let first = try #require(full.first ?? nil)
        #expect(first.title == "Song")
        #expect(first.durationMs == 200_000)
        #expect(first.elapsedMs == 12_500)
        #expect(first.reportedAt == Date(timeIntervalSince1970: 1_763_200_000))
        #expect(first.playing)

        let diff = p.feed(line(#"{"type":"data","diff":true,"payload":{"playing":false,"album":null}}"#))
        let second = try #require(diff.first ?? nil)
        #expect(second.title == "Song")
        #expect(second.album == "")
        #expect(!second.playing)
    }

    @Test func splitChunksAndEmptyPayload() {
        let p = AdapterStreamParser()
        let bytes = line(#"{"type":"data","diff":false,"payload":{"bundleIdentifier":"a","title":"T","playing":true}}"#)
        #expect(p.feed(bytes.prefix(20)).isEmpty)
        let out = p.feed(bytes.dropFirst(20))
        #expect(out.count == 1)
        #expect(out[0]?.title == "T")
        // Nothing playing: an empty full payload.
        let cleared = p.feed(line(#"{"type":"data","diff":false,"payload":{}}"#))
        #expect(cleared.count == 1)
        #expect(cleared[0] == nil)
    }

    @Test func parentBundleWinsForBrowserTabs() {
        let info = AdapterStreamParser.info(from: ["bundleIdentifier": "com.google.Chrome.helper", "parentApplicationBundleIdentifier": "com.google.Chrome", "title": "x"])
        #expect(info?.bundleID == "com.google.Chrome")
    }
}

@Suite struct PlayerNotificationTests {
    @Test func spotifyPayload() {
        let info = PlayerNotificationSource.info(from: [
            "Player State": "Playing", "Name": "Song", "Artist": "A", "Album": "B", "Duration": 215_000, "Playback Position": 12.5,
        ], bundleID: "com.spotify.client")
        #expect(info?.durationMs == 215_000)
        #expect(info?.elapsedMs == 12_500)
        #expect(info?.playing == true)
    }

    @Test func stoppedIsNothing() {
        #expect(PlayerNotificationSource.info(from: ["Player State": "Stopped", "Name": "x"], bundleID: "com.apple.Music") == nil)
    }
}

@Suite struct SnapGeometryTests {
    let screen = CGRect(x: 0, y: 0, width: 1440, height: 875)
    let m = SnapGeometry.margin

    @Test func snapsToNearestCorner() {
        let f = SnapGeometry.settle(CGRect(x: 1300, y: 760, width: 320, height: 118), in: screen)
        #expect(f.maxX == screen.maxX - m)
        #expect(f.maxY == screen.maxY - m)
    }

    @Test func middleOfScreenSnapsToNearerEdge() {
        let f = SnapGeometry.settle(CGRect(x: 300, y: 400, width: 320, height: 118), in: screen)
        #expect(f.minX == m)
        #expect(f.minY == 400)
    }

    @Test func edgeResizeMovesOnlyThatEdgeAndClamps() {
        let start = CGRect(x: 100, y: 100, width: 320, height: 118)
        let lo = CGSize(width: 260, height: 100), hi = CGSize(width: 800, height: 240)
        let wider = SnapGeometry.resized(start, edges: .left, by: CGVector(dx: -50, dy: 0), min: lo, max: hi)
        #expect(wider == CGRect(x: 50, y: 100, width: 370, height: 118))
        // Dragging the top edge down (y decreases) shrinks it, bottom edge fixed, clamped at the minimum.
        let shorter = SnapGeometry.resized(start, edges: [.top, .right], by: CGVector(dx: 1000, dy: -80), min: lo, max: hi)
        #expect(shorter == CGRect(x: 100, y: 100, width: 800, height: 100))
        let taller = SnapGeometry.resized(start, edges: .bottom, by: CGVector(dx: 0, dy: -60), min: lo, max: hi)
        #expect(taller == CGRect(x: 100, y: 40, width: 320, height: 178))
    }

    @Test func heightPicksTheLayout() {
        #expect(OverlayLayout.forHeight(44) == .pill)
        #expect(OverlayLayout.forHeight(123.5) == .pill)
        #expect(OverlayLayout.forHeight(124) == .card)
        #expect(OverlayLayout.forHeight(199.5) == .card)
        #expect(OverlayLayout.forHeight(200) == .lyrics)
        for layout in OverlayLayout.allCases {
            #expect(OverlayLayout.forHeight(layout.preset.height) == layout)
        }
    }

    @Test func pillGrowsIntoTwoLinesThenTall() {
        let one = PillMetrics(size: CGSize(width: 400, height: 48))
        #expect(!one.twoLine && !one.tall && !one.compact)
        #expect(one.art == 36)
        let two = PillMetrics(size: CGSize(width: 400, height: 80))
        #expect(two.twoLine && !two.tall)
        let tall = PillMetrics(size: CGSize(width: 400, height: 110))
        #expect(tall.twoLine && tall.tall)
        #expect(tall.lineFont > two.lineFont && two.lineFont >= one.lineFont)
        // Narrow: previous/next go before the text is cut to a word or two.
        #expect(PillMetrics(size: CGSize(width: 280, height: 48)).compact)
        #expect(PillMetrics(size: CGSize(width: 720, height: 123)).art == 111)
    }

    @Test func cardCoverGrowsWithHeight() {
        #expect(CardView.art(height: 150, lyricLine: true) == 64)
        #expect(CardView.art(height: 124, lyricLine: true) == 40)
        #expect(CardView.art(height: 150, lyricLine: false) == 88)
    }

    @Test func resizeKeepsTheAnchoredCorner() {
        let card = CGRect(x: 1440 - m - 320, y: 875 - m - 118, width: 320, height: 118)
        let pill = SnapGeometry.resize(card, to: CGSize(width: 400, height: 48), in: screen)
        #expect(pill.maxX == card.maxX)
        #expect(pill.maxY == card.maxY)
    }
}

@Suite struct ColorTests {
    @Test func oklchRoundTrips() {
        for rgb in [Rgb(r: 255, g: 138, b: 92), Rgb(r: 18, g: 10, b: 25), Rgb(r: 150, g: 113, b: 255)] {
            let c = OKLCH(rgb: rgb).srgb
            #expect(abs(c.r * 255 - Double(rgb.r)) < 0.6)
            #expect(abs(c.g * 255 - Double(rgb.g)) < 0.6)
            #expect(abs(c.b * 255 - Double(rgb.b)) < 0.6)
        }
    }

    @Test func hueTakesTheShortWayRound() {
        let a = OKLCH(l: 0.5, c: 0.1, h: 350)
        let b = OKLCH(l: 0.5, c: 0.1, h: 10).unwrapped(near: a)
        #expect(b.h == 370)
    }

    @Test func lyricWindowCentresCurrentAndPinsEnds() {
        let hs: [CGFloat] = [32, 64, 32, 32, 96, 32, 32] // wrapped lines are taller
        #expect(LyricsWindow.offset(heights: hs, index: nil, viewport: 100) == 0)
        #expect(LyricsWindow.offset(heights: hs, index: 0, viewport: 100) == 0)
        // Line 4 spans 160...256; its centre (208) lands mid-window.
        #expect(LyricsWindow.offset(heights: hs, index: 4, viewport: 100) == 158)
        #expect(LyricsWindow.offset(heights: hs, index: 6, viewport: 100) == 220)
        #expect(LyricsWindow.offset(heights: [32, 32], index: 1, viewport: 100) == 0)
    }

    @Test func marqueeCycle() {
        let o = 64.0, v = 32.0
        #expect(MarqueeText.offset(at: 1, overflow: o, speed: v) == 0)
        #expect(abs(MarqueeText.offset(at: MarqueeText.hold + 1, overflow: o, speed: v) - 32) < 0.001)
        #expect(MarqueeText.offset(at: MarqueeText.hold + 2 + 1, overflow: o, speed: v) == 64)
        let period = MarqueeText.hold * 2 + 2 + MarqueeText.back
        #expect(abs(MarqueeText.offset(at: period + 0.5, overflow: o, speed: v)) < 0.001)
    }
}
