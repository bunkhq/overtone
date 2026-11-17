import AppKit
import Foundation
import Observation
import OvertoneCore
import os

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "model")

public enum LyricsState: Equatable, Sendable {
    /// No lookup (no artist, or lyrics unavailable).
    case none
    case loading
    case instrumental
    /// Unsynced lyrics: there is no position to follow, so the overlay doesn't scroll them.
    case unsynced
    case synced([LyricLine])
}

/// Where the progress bar is and where it's heading. The bar runs one
/// compositor animation from `fraction` to 1 over `remaining`, so nothing in
/// the app ticks per frame. `epoch` changes whenever it must restart.
public struct ProgressState: Equatable, Sendable {
    public var fraction: Double
    /// Wall seconds until the end, `nil` when paused.
    public var remaining: TimeInterval?
    public var epoch: Int
}

/// Milliseconds on a monotonic clock, the timebase handed to the core's clock.
func monotonicMs() -> UInt64 { DispatchTime.now().uptimeNanoseconds / 1_000_000 }

/// The current track, its colours, its lyrics and the playback clock.
/// Event-driven: it reacts to source updates and arms at most one timer (the
/// next lyric line). See docs/ARCHITECTURE.md §4–5.
@MainActor
@Observable
public final class NowPlayingModel {
    public private(set) var info: NowPlayingInfo?
    public private(set) var artwork: CGImage?
    public private(set) var backdrop: CGImage?
    public private(set) var colors: OverlayColors = .neutral
    public private(set) var lyrics: LyricsState = .none
    /// Whether the overlay keeps room for lyrics. False for songs without synced
    /// lyrics (the card drops its lyric line, the lyric panel folds); while a lookup
    /// runs the previous value holds, so a song change doesn't flicker.
    public private(set) var lyricsSpace = true
    /// Index into the synced lyrics of the line being sung, `nil` before the first.
    public private(set) var lineIndex: Int?
    /// `nil` when the source reports no timeline: the bar hides.
    public private(set) var progress: ProgressState?

    public var forcedTone: Tone? {
        didSet { if forcedTone != oldValue { retone() } }
    }

    @ObservationIgnored private var clock = PlaybackClock(durationMs: nil)
    @ObservationIgnored private var lyricsDoc: LyricsDoc?
    @ObservationIgnored private let lyricsClient: LyricsClient
    @ObservationIgnored private var lyricsTask: Task<Void, Never>?
    @ObservationIgnored private var lineTimer: Timer?
    @ObservationIgnored private var pending: NowPlayingInfo?
    @ObservationIgnored private var debounce: Task<Void, Never>?
    @ObservationIgnored private var clearTask: Task<Void, Never>?
    @ObservationIgnored private var artworkTask: Task<Void, Never>?
    @ObservationIgnored private var artworkDigest: String?
    @ObservationIgnored private var artworkSample: Data?
    @ObservationIgnored private var paletteCache: [String: ProcessedArtwork] = [:]
    @ObservationIgnored private var paletteCacheOrder: [String] = []
    @ObservationIgnored private var progressEpoch = 0

    /// Players often send title, then artwork, then timeline as separate events.
    static let trackDebounce: Duration = .milliseconds(150)
    /// Sources briefly report "nothing" between tracks; don't flash the overlay away.
    static let clearGrace: Duration = .milliseconds(1500)

    public init(lyricsCacheDir: URL?) {
        lyricsClient = LyricsClient(cacheDir: lyricsCacheDir?.path)
    }

    // MARK: Source events

    public func ingest(_ update: NowPlayingInfo?) {
        guard let update else {
            pending = nil
            debounce?.cancel()
            scheduleClear()
            return
        }
        clearTask?.cancel()
        clearTask = nil

        if let current = info, current.trackKey == update.trackKey, pending == nil {
            applyPlayback(update)
            return
        }
        pending = update
        debounce?.cancel()
        debounce = Task { [weak self] in
            try? await Task.sleep(for: Self.trackDebounce)
            guard !Task.isCancelled, let self, let next = self.pending else { return }
            self.pending = nil
            self.applyTrack(next)
        }
    }

    private func scheduleClear() {
        guard clearTask == nil, info != nil else { return }
        clearTask = Task { [weak self] in
            try? await Task.sleep(for: Self.clearGrace)
            guard !Task.isCancelled, let self else { return }
            self.clearTask = nil
            self.clear()
        }
    }

    private func clear() {
        info = nil
        lyricsTask?.cancel()
        lyrics = .none
        lyricsDoc = nil
        lineIndex = nil
        progress = nil
        lineTimer?.invalidate()
    }

    private func applyTrack(_ next: NowPlayingInfo) {
        let previous = info
        info = next
        clock = PlaybackClock(durationMs: next.durationMs)
        _ = report(next)
        updateArtwork(next.artwork, newTrack: true)
        if previous?.title != next.title || previous?.artist != next.artist || previous?.album != next.album {
            fetchLyrics(for: next)
        }
        rearm()
    }

    private func applyPlayback(_ next: NowPlayingInfo) {
        let durationChanged = next.durationMs != info?.durationMs
        info = next
        if durationChanged {
            clock = PlaybackClock(durationMs: next.durationMs)
        }
        let change = report(next)
        if next.artwork != nil { updateArtwork(next.artwork, newTrack: false) }
        if change == .reanchored || durationChanged { rearm() }
    }

    private func report(_ i: NowPlayingInfo) -> ClockChange {
        let now = monotonicMs()
        let age = i.reportedAt.map { max(0, Date().timeIntervalSince($0) * 1000) } ?? 0
        let reportedAt = now > UInt64(age) ? now - UInt64(age) : 0
        return clock.report(positionMs: i.elapsedMs ?? 0, reportedAtMs: reportedAt, playing: i.playing, rate: i.rate, nowMs: now)
    }

    /// Current position in ms (for time labels).
    public func positionMs() -> UInt64 { clock.positionAt(nowMs: monotonicMs()) }

    /// Wall-clock ms until the next lyric line, for timing a long line's glide; nil when paused or unknown.
    public func msToNextLine() -> UInt64? {
        guard let doc = lyricsDoc, doc.synced(), clock.playing() else { return nil }
        let now = monotonicMs()
        guard let next = doc.nextChangeMs(positionMs: clock.positionAt(nowMs: now)) else { return nil }
        return clock.wallMsUntil(targetMs: next, nowMs: now)
    }

    /// Seeking needs a timeline to land on.
    public var canSeek: Bool { info?.hasTimeline == true }

    /// Moves the clock to `ms` at once, so the progress bar and lyrics follow the
    /// click instead of waiting for the player to confirm the seek.
    public func seekLocally(to ms: UInt64) {
        guard let info, info.hasTimeline else { return }
        let target = min(ms, info.durationMs ?? ms)
        let now = monotonicMs()
        _ = clock.report(positionMs: target, reportedAtMs: now, playing: clock.playing(), rate: clock.rate(), nowMs: now)
        rearm()
    }

    // MARK: Progress and lyric timing

    private func rearm() {
        let now = monotonicMs()
        if let info, info.hasTimeline, let fraction = clock.progressAt(nowMs: now) {
            progressEpoch += 1
            let remaining = clock.remainingWallMs(nowMs: now).map { TimeInterval($0) / 1000 }
            progress = ProgressState(fraction: fraction, remaining: remaining, epoch: progressEpoch)
        } else {
            progress = nil
        }
        updateLine()
    }

    private func updateLine() {
        lineTimer?.invalidate()
        lineTimer = nil
        guard let doc = lyricsDoc, doc.synced(), info?.hasTimeline == true else {
            lineIndex = nil
            return
        }
        let now = monotonicMs()
        let pos = clock.positionAt(nowMs: now)
        let idx = doc.lineAt(positionMs: pos).map(Int.init)
        if idx != lineIndex { lineIndex = idx }
        guard let next = doc.nextChangeMs(positionMs: pos), let wait = clock.wallMsUntil(targetMs: next, nowMs: now) else { return }
        let t = Timer(timeInterval: TimeInterval(wait + 8) / 1000, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.updateLine() }
        }
        t.tolerance = 0.01
        RunLoop.main.add(t, forMode: .common)
        lineTimer = t
    }

    // MARK: Lyrics

    private func fetchLyrics(for i: NowPlayingInfo) {
        lyricsTask?.cancel()
        lyricsDoc = nil
        lineIndex = nil
        let track = Track(title: i.title, artist: i.artist, album: i.album, durationMs: i.durationMs)
        guard !track.title.isEmpty, !track.artist.isEmpty else {
            lyrics = .none
            lyricsSpace = false
            return
        }
        lyrics = .loading
        let client = lyricsClient
        let key = i.trackKey
        lyricsTask = Task { [weak self] in
            let outcome = await Task.detached(priority: .utility) { () -> Result<LyricsOutcome, Error> in
                Result { try client.lookup(track: track) }
            }.value
            guard !Task.isCancelled, let self, self.info?.trackKey == key || self.pending?.trackKey == key else { return }
            switch outcome {
            case let .success(.found(doc)):
                self.lyricsDoc = doc
                self.lyrics = doc.synced() ? .synced(doc.lines()) : .unsynced
            case .success(.instrumental):
                self.lyrics = .instrumental
            case .success(.notFound):
                self.lyrics = .none
            case let .failure(error):
                log.error("lyrics lookup failed: \(error.localizedDescription)")
                self.lyrics = .none
            }
            if case let .synced(lines) = self.lyrics { self.lyricsSpace = !lines.isEmpty } else { self.lyricsSpace = false }
            self.updateLine()
        }
    }

    // MARK: Artwork and palette

    private func updateArtwork(_ data: Data?, newTrack: Bool) {
        guard let data else {
            if newTrack {
                // Don't show the last track's cover on this one. Keep its colours
                // briefly: artwork usually follows within a second.
                artwork = nil
                backdrop = nil
                artworkDigest = nil
                artworkSample = nil
                artworkTask?.cancel()
                artworkTask = Task { [weak self] in
                    try? await Task.sleep(for: .seconds(1.5))
                    guard !Task.isCancelled, let self, self.artworkDigest == nil else { return }
                    self.setColors(neutralPalette())
                }
            }
            return
        }
        let digest = ArtworkProcessor.digest(data)
        guard digest != artworkDigest else { return }
        artworkDigest = digest
        artworkTask?.cancel()
        if let hit = paletteCache[digest] { // cleared on tone change, so always current
            apply(hit)
            return
        }
        let tone = forcedTone
        artworkTask = Task { [weak self] in
            let processed = await Task.detached(priority: .userInitiated) { ArtworkProcessor.process(data, tone: tone) }.value
            guard !Task.isCancelled, let self, self.artworkDigest == digest else { return }
            if let processed {
                self.remember(processed, for: digest)
                self.apply(processed)
            } else {
                self.setColors(neutralPalette())
            }
        }
    }

    private func apply(_ p: ProcessedArtwork) {
        artwork = p.image
        backdrop = p.backdrop
        artworkSample = p.sample
        setColors(p.palette)
    }

    private func remember(_ p: ProcessedArtwork, for digest: String) {
        paletteCache[digest] = p
        paletteCacheOrder.removeAll { $0 == digest }
        paletteCacheOrder.append(digest)
        if paletteCacheOrder.count > 24 {
            paletteCache.removeValue(forKey: paletteCacheOrder.removeFirst())
        }
    }

    /// The current cover's palette; nil without artwork (the coloured menu-bar icon uses it).
    public private(set) var artworkPalette: OverlayPalette?

    private func setColors(_ palette: OverlayPalette) {
        let next = OverlayColors(palette, near: colors)
        if next != colors { colors = next }
        let fromArtwork: OverlayPalette? = artworkSample == nil ? nil : palette
        if fromArtwork != artworkPalette { artworkPalette = fromArtwork }
    }

    private func retone() {
        paletteCache.removeAll()
        paletteCacheOrder.removeAll()
        guard let sample = artworkSample else {
            setColors(neutralPalette())
            return
        }
        let tone = forcedTone
        Task { [weak self] in
            let palette = await Task.detached { ArtworkProcessor.palette(sample: sample, tone: tone) }.value
            guard let self, self.artworkSample == sample else { return }
            self.setColors(palette)
        }
    }
}
