import Foundation
import OvertoneCore

/// "Music only", ported from the Windows app: lets through only items that are
/// music, so a video in a browser tab doesn't take over the overlay or the
/// Discord status. In order: a dedicated music player always is; an item with an
/// album and artist is (YouTube Music, Spotify, Deezer on the web); one the OS
/// marks as video isn't; anything else must be a song iTunes or Deezer knows (an
/// official music video passes, a random video doesn't; offline gives the
/// benefit of the doubt). Verdicts are cached per song.
@MainActor
final class MusicGate {
    enum Verdict: Equatable { case music, notMusic, ask }

    private let settings: Settings
    private let deliver: (NowPlayingInfo?) -> Void
    private var verdicts: [String: Bool] = [:]
    private var asking: Set<String> = []
    private var latest: NowPlayingInfo?

    static let musicApps = [
        "spotify", "com.apple.music", "itunes", "cider", "tidal", "deezer", "amazon.music", "qobuz", "foobar2000",
        "ytmdesktop", "th-ch", "youtube-music", "soundcloud", "swinsian", "coppertino", "plexamp", "doppler", "musicbee",
    ]

    init(settings: Settings, deliver: @escaping (NowPlayingInfo?) -> Void) {
        self.settings = settings
        self.deliver = deliver
    }

    /// The quick part of the decision; `.ask` means the catalogs decide.
    static func verdict(_ i: NowPlayingInfo) -> Verdict {
        let id = (i.bundleID ?? "").lowercased()
        if musicApps.contains(where: id.contains) { return .music }
        if !i.album.trimmingCharacters(in: .whitespaces).isEmpty, !i.artist.trimmingCharacters(in: .whitespaces).isEmpty { return .music }
        if i.mediaType?.lowercased().contains("video") == true { return .notMusic }
        if i.artist.trimmingCharacters(in: .whitespaces).isEmpty || i.title.trimmingCharacters(in: .whitespaces).isEmpty { return .notMusic }
        return .ask
    }

    private static func key(_ i: NowPlayingInfo) -> String { "\(i.bundleID ?? "")\u{1}\(i.artist)\u{1}\(i.title)" }

    func ingest(_ info: NowPlayingInfo?) {
        latest = info
        guard let info, settings.musicOnly else { return deliver(info) }
        switch Self.verdict(info) {
        case .music: deliver(info)
        case .notMusic: deliver(nil)
        case .ask:
            let key = Self.key(info)
            if let known = verdicts[key] { return deliver(known ? info : nil) }
            deliver(nil) // not shown until the catalogs say it's a song
            guard asking.insert(key).inserted else { return }
            let track = Track(title: info.title, artist: info.artist, album: info.album, durationMs: info.durationMs)
            Task { [weak self] in
                let found = await Task.detached(priority: .utility) { isCatalogSong(track: track) }.value
                guard let self else { return }
                self.asking.remove(key)
                if let found { self.verdicts[key] = found }
                // Offline: the benefit of the doubt (not cached, so it's asked again later).
                let music = found ?? true
                if let latest = self.latest, Self.key(latest) == key { self.deliver(music ? latest : nil) }
            }
        }
    }

    /// The setting changed: re-decide the item on screen.
    func reevaluate() { ingest(latest) }
}
