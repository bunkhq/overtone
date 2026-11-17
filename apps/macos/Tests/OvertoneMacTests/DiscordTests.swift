import Darwin
import Foundation
import OvertoneCore
import Testing
@testable import OvertoneMac

/// A one-connection fake of Discord's IPC socket: answers the handshake with
/// READY and records every frame it receives.
private final class FakeDiscord: @unchecked Sendable {
    let path: String
    private let server: Int32
    private let lock = NSLock()
    private var frames: [(op: Int32, json: [String: Any])] = []

    init() {
        path = NSTemporaryDirectory() + "ovt-ipc-\(UUID().uuidString.prefix(8))"
        server = socket(AF_UNIX, SOCK_STREAM, 0)
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        withUnsafeMutableBytes(of: &addr.sun_path) { buf in
            let b = Array(path.utf8)
            buf.copyBytes(from: b)
            buf[b.count] = 0
        }
        _ = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(server, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        listen(server, 1)
        let thread = Thread { [self] in serve() }
        thread.start()
    }

    var received: [(op: Int32, json: [String: Any])] { lock.withLock { frames } }

    private func serve() {
        let c = accept(server, nil, nil)
        guard c >= 0 else { return }
        while let (op, json) = readFrame(c) {
            lock.withLock { frames.append((op, json)) }
            if op == 0 { writeFrame(c, op: 1, json: ["cmd": "DISPATCH", "evt": "READY"]) }
        }
    }

    private func readFrame(_ c: Int32) -> (Int32, [String: Any])? {
        var header = [UInt8](repeating: 0, count: 8)
        guard Darwin.read(c, &header, 8) == 8 else { return nil }
        let op = header.withUnsafeBytes { $0.load(fromByteOffset: 0, as: Int32.self) }
        let len = Int(header.withUnsafeBytes { $0.load(fromByteOffset: 4, as: Int32.self) })
        var body = [UInt8](repeating: 0, count: len)
        var got = 0
        while got < len { let n = body.withUnsafeMutableBytes { Darwin.read(c, $0.baseAddress! + got, len - got) }; if n <= 0 { return nil }; got += n }
        return (op, (try? JSONSerialization.jsonObject(with: Data(body))) as? [String: Any] ?? [:])
    }

    private func writeFrame(_ c: Int32, op: Int32, json: [String: Any]) {
        let body = try! JSONSerialization.data(withJSONObject: json)
        var frame = Data()
        withUnsafeBytes(of: op.littleEndian) { frame.append(contentsOf: $0) }
        withUnsafeBytes(of: Int32(body.count).littleEndian) { frame.append(contentsOf: $0) }
        frame.append(body)
        _ = frame.withUnsafeBytes { Darwin.write(c, $0.baseAddress!, frame.count) }
    }

    deinit { Darwin.close(server); unlink(path) }
}

@Suite struct DiscordTests {
    @Test func handshakeThenActivityFrames() async throws {
        let fake = FakeDiscord()
        let ipc = DiscordIPC(clientID: "1234567890", paths: [fake.path])
        #expect(ipc.connect())
        try ipc.setActivity(["type": 2, "details": "Song", "state": "Artist", "assets": ["large_text": "a lyric line"]])
        try ipc.setActivity(nil)
        try await Task.sleep(for: .milliseconds(200))
        let frames = fake.received
        #expect(frames.count == 3)
        #expect(frames[0].op == 0 && frames[0].json["client_id"] as? String == "1234567890")
        let set = try #require(frames[1].json["args"] as? [String: Any])
        #expect(frames[1].json["cmd"] as? String == "SET_ACTIVITY")
        #expect(set["pid"] as? Int == Int(getpid()))
        #expect(((set["activity"] as? [String: Any])?["assets"] as? [String: Any])?["large_text"] as? String == "a lyric line")
        let clear = try #require(frames[2].json["args"] as? [String: Any])
        #expect(clear["activity"] is NSNull) // clearing sends an explicit null
        ipc.close()
    }

    @Test @MainActor func textFieldsFitDiscordLimits() {
        #expect(DiscordPresence.fit("a").count == 2)
        #expect(DiscordPresence.fit(String(repeating: "x", count: 300)).count == 128)
        #expect(DiscordPresence.fit("  Song  ") == "Song")
    }

    @Test @MainActor func playersMapToIcons() {
        #expect(DiscordPresence.service("com.spotify.client").key == "spotify")
        #expect(DiscordPresence.service("com.apple.Music").key == "applemusic")
        #expect(DiscordPresence.service("com.google.Chrome").name == "Google Chrome")
        #expect(DiscordPresence.service("com.apple.Safari").domain == "www.apple.com/safari")
    }
}

@Suite struct LyricRowTests {
    @Test func rowsKeepTheSamePaddingWhateverTheirLength() {
        let one: CGFloat = 21, three: CGFloat = 65
        // Air above and below the drawn text is the same for one line and three, dimmed or current.
        for text in [one, three] {
            for current in [true, false] {
                let drawn = text * (current ? 1 : LyricLineView.dimScale)
                #expect(abs(LyricLineView.rowHeight(text: text, current: current) - drawn - 2 * LyricLineView.padding) < 0.001)
            }
        }
    }
}

@Suite struct CompactLineTests {
    typealias G = MarqueeText.Glide

    @Test func glideReadsAtAComfortablePaceWhenThereIsTime() {
        // 90 pt at 45 pt/s = 2 s, with 10 s until the next line.
        #expect(abs(G.duration(overflow: 90, msToNext: 10_000) - 2) < 0.001)
        #expect(abs(G.duration(overflow: 90, msToNext: nil) - 2) < 0.001)
    }

    @Test func glideHurriesToLandBeforeTheNextLineButNotPastTheFastestPace() {
        // 3 s left: 3 − 0.9 start − 0.7 hold = 1.4 s.
        #expect(abs(G.duration(overflow: 90, msToNext: 3_000) - 1.4) < 0.001)
        // Very little time: capped at 140 pt/s (280 pt → 2 s).
        #expect(abs(G.duration(overflow: 280, msToNext: 1_000) - 2) < 0.001)
    }

    @Test func scrubFractionClampsToTheBar() {
        #expect(ScrubBar.fraction(x: 50, width: 200) == 0.25)
        #expect(ScrubBar.fraction(x: -10, width: 200) == 0)
        #expect(ScrubBar.fraction(x: 400, width: 200) == 1)
        #expect(ScrubBar.fraction(x: 10, width: 0) == 0)
    }
}

@Suite struct FilterAndToneTests {
    @Test func filterLevelsThroughTheCore() {
        #expect(censor(text: "Ain't that some shit?", level: .all) == "Ain't that some ****?")
        #expect(censor(text: "Ain't that some shit?", level: .slurs) == "Ain't that some shit?")
        #expect(censor(text: "Ain't that some shit?", level: .off) == "Ain't that some shit?")
    }

    @Test func matchSystemFollowsTheAppearance() {
        #expect(TonePreference.system.forced(systemDark: true) == .dark)
        #expect(TonePreference.system.forced(systemDark: false) == .light)
        #expect(TonePreference.auto.forced(systemDark: true) == nil)
        #expect(TonePreference.light.forced(systemDark: true) == .light)
    }
}

@Suite struct ParityTests {
    @Test func foldsWithoutLyrics() {
        let card = CGSize(width: 340, height: 150), panel = CGSize(width: 360, height: 232), pill = CGSize(width: 400, height: 48)
        #expect(OverlayLayout.drawnSize(user: card, lyricsSpace: true) == card)
        #expect(OverlayLayout.drawnSize(user: card, lyricsSpace: false).height == 126)
        #expect(OverlayLayout.drawnSize(user: panel, lyricsSpace: false).height == OverlayLayout.foldedLyricsHeight)
        #expect(OverlayLayout.drawnSize(user: pill, lyricsSpace: false) == pill)
    }

    @Test func draggingAFoldedCardKeepsItsLyricRowStored() {
        // Drawn 126 without lyrics = a 150 card; below 100 it's a pill; it stops at the tallest card.
        #expect(OverlayLayout.userSize(drawn: CGSize(width: 340, height: 126), lyricsSpace: false).height == 150)
        #expect(OverlayLayout.userSize(drawn: CGSize(width: 340, height: 80), lyricsSpace: false).height == 80)
        #expect(OverlayLayout.userSize(drawn: CGSize(width: 340, height: 400), lyricsSpace: false).height == 199)
        #expect(OverlayLayout.userSize(drawn: CGSize(width: 340, height: 400), lyricsSpace: true).height == 400)
    }

    @Test @MainActor func musicRules() {
        func info(_ bundle: String, title: String = "Song", artist: String = "Artist", album: String = "", type: String? = nil) -> NowPlayingInfo {
            NowPlayingInfo(bundleID: bundle, title: title, artist: artist, album: album, mediaType: type)
        }
        #expect(MusicGate.verdict(info("com.spotify.client", album: "")) == .music)
        #expect(MusicGate.verdict(info("com.google.Chrome", album: "LP")) == .music)
        #expect(MusicGate.verdict(info("com.google.Chrome", type: "MRMediaRemoteMediaTypeVideo")) == .notMusic)
        #expect(MusicGate.verdict(info("com.google.Chrome", artist: "")) == .notMusic)
        #expect(MusicGate.verdict(info("com.google.Chrome")) == .ask)
    }
}

@Suite struct GlassTests {
    /// WCAG contrast of two OKLCH colours.
    private func contrast(_ a: OKLCH, _ b: OKLCH) -> Double {
        func lum(_ c: OKLCH) -> Double {
            let s = c.srgb
            func f(_ v: Double) -> Double { v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
            return 0.2126 * f(s.r) + 0.7152 * f(s.g) + 0.0722 * f(s.b)
        }
        let (x, y) = (lum(a), lum(b))
        return (max(x, y) + 0.05) / (min(x, y) + 0.05)
    }

    @Test func glassTextFollowsTheGlassAppearanceNotTheCover() {
        // A pale cover (light tone) on dark glass still gets light text, and vice versa.
        let pale = OverlayColors(paletteFromRgba(rgba: Data([UInt8](repeating: 0, count: 0) + Array([UInt8]([240, 235, 225, 255]).repeated(16))), width: 4, height: 4, forceTone: .light))
        for dark in [true, false] {
            let g = pale.onGlass(dark: dark)
            #expect(g.tone == (dark ? Tone.dark : Tone.light))
            #expect(contrast(g.ink, g.base) >= 7)
            #expect(contrast(g.inkSoft, g.base) >= 4.5)
            #expect(contrast(g.inkFaint, g.base) >= 3)
        }
    }
}

private extension Array {
    func repeated(_ n: Int) -> [Element] { (0..<n).flatMap { _ in self } }
}
