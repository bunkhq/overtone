import Foundation

/// What the OS reports about the current media session.
public struct NowPlayingInfo: Equatable, Sendable {
    /// The app reporting the session (e.g. `com.spotify.client`).
    public var bundleID: String?
    public var title: String
    /// Empty when the source sends none.
    public var artist: String
    public var album: String
    public var durationMs: UInt64?
    /// Position that was true at `reportedAt`. `nil` when the source has no timeline.
    public var elapsedMs: UInt64?
    /// When `elapsedMs` was true.
    public var reportedAt: Date?
    public var playing: Bool
    public var rate: Double
    /// Encoded image bytes (JPEG/PNG), when the source provides artwork.
    public var artwork: Data?
    /// MediaRemote's media type when reported (e.g. `MRMediaRemoteMediaTypeMusic`, a video type).
    public var mediaType: String?

    public init(
        bundleID: String? = nil, title: String, artist: String = "", album: String = "",
        durationMs: UInt64? = nil, elapsedMs: UInt64? = nil, reportedAt: Date? = nil,
        playing: Bool = false, rate: Double = 1, artwork: Data? = nil, mediaType: String? = nil
    ) {
        self.bundleID = bundleID
        self.title = title
        self.artist = artist
        self.album = album
        self.durationMs = durationMs
        self.elapsedMs = elapsedMs
        self.reportedAt = reportedAt
        self.playing = playing
        self.rate = rate
        self.artwork = artwork
        self.mediaType = mediaType
    }

    /// Identifies the track, independent of playback state.
    public var trackKey: String { [bundleID ?? "", title, artist, album].joined(separator: "\u{1f}") }

    public var hasTimeline: Bool { elapsedMs != nil && (durationMs ?? 0) > 0 }
}

public enum TransportCommand: Sendable, Equatable {
    case togglePlayPause
    case next
    case previous
    case seek(ms: UInt64)
}

/// Where now-playing information comes from. Kept behind a protocol because
/// the primary macOS source relies on a private framework Apple can close off
/// at any time (docs/ARCHITECTURE.md §1).
@MainActor
public protocol NowPlayingSource: AnyObject {
    /// Called on the main actor with the full current state; `nil` when nothing is playing.
    var onUpdate: ((NowPlayingInfo?) -> Void)? { get set }
    var name: String { get }
    func start()
    func stop()
    func send(_ command: TransportCommand)
}
