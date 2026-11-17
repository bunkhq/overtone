import AppKit
import Foundation

/// Fallback source for when the MediaRemote adapter stops working after an OS
/// update: Spotify and Music broadcast distributed notifications on every
/// change. No artwork, and Music sends no position, but it keeps the two
/// biggest players working. Controls go through AppleScript, which asks the
/// user for Automation permission the first time.
@MainActor
public final class PlayerNotificationSource: NowPlayingSource {
    public var onUpdate: ((NowPlayingInfo?) -> Void)?
    public let name = "Spotify and Music"

    private struct Player {
        let notification: String
        let bundleID: String
        let previous: String
    }

    private static let players = [
        Player(notification: "com.spotify.client.PlaybackStateChanged", bundleID: "com.spotify.client", previous: "previous track"),
        Player(notification: "com.apple.Music.playerInfo", bundleID: "com.apple.Music", previous: "back track"),
    ]

    private var observers: [NSObjectProtocol] = []
    private var current: NowPlayingInfo?

    public init() {}

    public func start() {
        let center = DistributedNotificationCenter.default()
        for player in Self.players {
            let token = center.addObserver(forName: .init(player.notification), object: nil, queue: .main) { [weak self] note in
                let info = Self.info(from: note.userInfo ?? [:], bundleID: player.bundleID)
                MainActor.assumeIsolated { self?.handle(info, from: player.bundleID) }
            }
            observers.append(token)
        }
    }

    public func stop() {
        let center = DistributedNotificationCenter.default()
        observers.forEach(center.removeObserver)
        observers.removeAll()
    }

    public func send(_ command: TransportCommand) {
        guard let id = current?.bundleID, let player = Self.players.first(where: { $0.bundleID == id }) else { return }
        let verb: String
        switch command {
        case .togglePlayPause: verb = "playpause"
        case .next: verb = "next track"
        case .previous: verb = player.previous
        case let .seek(ms): verb = "set player position to \(Double(ms) / 1000)"
        }
        var error: NSDictionary?
        NSAppleScript(source: "tell application id \"\(id)\" to \(verb)")?.executeAndReturnError(&error)
    }

    private func handle(_ info: NowPlayingInfo?, from bundleID: String) {
        // A player stopping only clears the overlay if it was the one showing.
        if info == nil, current?.bundleID != bundleID { return }
        current = info
        onUpdate?(info)
    }

    nonisolated static func info(from u: [AnyHashable: Any], bundleID: String) -> NowPlayingInfo? {
        let state = u["Player State"] as? String
        guard state == "Playing" || state == "Paused", let title = u["Name"] as? String, !title.isEmpty else { return nil }
        let durationMs = ((u["Duration"] ?? u["Total Time"]) as? NSNumber).map { UInt64(max($0.doubleValue, 0)) }
        let positionS = (u["Playback Position"] as? NSNumber)?.doubleValue
        return NowPlayingInfo(
            bundleID: bundleID,
            title: title,
            artist: (u["Artist"] as? String) ?? "",
            album: (u["Album"] as? String) ?? "",
            durationMs: durationMs.flatMap { $0 > 0 ? $0 : nil },
            elapsedMs: positionS.map { UInt64(max($0, 0) * 1000) },
            reportedAt: positionS == nil ? nil : Date(),
            playing: state == "Playing",
            rate: 1
        )
    }
}
