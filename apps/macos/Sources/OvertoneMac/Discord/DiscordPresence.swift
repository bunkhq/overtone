import AppKit
import Foundation
import Observation
import OvertoneCore
import os

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "discord")

/// Discord Rich Presence, ported from the Windows app: "Listening to {artist}"
/// in the member list; on the card the title, artist and current lyric, the
/// album cover, the player's icon and a progress bar. See docs/DISCORD.md.
///
/// Discord accepts about five activity updates per 20 s, so updates are
/// coalesced to one every 4 s at most. Each send is built from the model *at
/// send time*: the lyric is the line playing then, and the progress bar's start
/// comes from the live clock. (Building it when the change happened and
/// sending it up to 4 s later shows a stale lyric and a progress bar that lags.)
@MainActor
final class DiscordPresence {
    static let minGap: TimeInterval = 4
    static let retry: Duration = .seconds(20)

    private let model: NowPlayingModel
    private let settings: Settings
    private var ipc: DiscordIPC?
    private var connecting = false
    private var retryTask: Task<Void, Never>?
    private var sendTask: Task<Void, Never>?
    private var lastSent: String?
    private var lastSendAt = Date.distantPast
    private var lastStart: Int64 = 0
    private var assets: Set<String> = []
    private var covers: [String: String?] = [:]

    init(model: NowPlayingModel, settings: Settings) {
        self.model = model
        self.settings = settings
        observe()
        if settings.discordEnabled { connect() }
    }

    /// Overtone's Discord application: its name heads the card and its art assets hold the player icons.
    static let defaultAppID = "1571417511696535643"

    private var appID: String {
        let override = settings.discordAppID?.trimmingCharacters(in: .whitespaces) ?? ""
        return override.isEmpty ? Self.defaultAppID : override
    }

    /// Turns presence on or off (menu).
    func setEnabled(_ on: Bool) {
        settings.discordEnabled = on
        if on {
            lastSent = nil
            connect()
        } else {
            sendTask?.cancel()
            retryTask?.cancel()
            let old = ipc
            ipc = nil
            lastSent = nil
            Task.detached { try? old?.setActivity(nil); old?.close() }
        }
    }

    /// Best effort on quit: clear the card so it doesn't linger until Discord notices.
    func shutdown() {
        guard let ipc else { return }
        try? ipc.setActivity(nil)
        ipc.close()
    }

    // MARK: Model changes

    private func observe() {
        withObservationTracking {
            // Everything the card shows: track, play state, seeks (progress epoch), lyric line.
            _ = model.info?.trackKey
            _ = model.info?.playing
            _ = model.progress?.epoch
            _ = model.lineIndex
            _ = model.lyrics
            _ = settings.discordStatusLine
            _ = settings.discordFilter
        } onChange: { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.observe()
                self.trackChanged()
                self.schedule()
            }
        }
    }

    private func trackChanged() {
        guard let info = model.info else { return }
        let key = Self.coverKey(info)
        guard covers[key] == nil else { return }
        covers[key] = .some(nil)
        let track = Track(title: info.title, artist: info.artist, album: info.album, durationMs: info.durationMs)
        Task { [weak self] in
            let url = await Task.detached(priority: .utility) { coverUrl(track: track) }.value
            guard let self, let url else { return }
            self.covers[key] = url
            self.schedule()
        }
    }

    // MARK: Sending

    private func schedule() {
        guard settings.discordEnabled, ipc?.connected == true else { return }
        let wait = lastSendAt.addingTimeInterval(Self.minGap).timeIntervalSinceNow
        if wait <= 0 {
            flush()
            return
        }
        guard sendTask == nil else { return } // the pending send picks up the latest state
        sendTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(wait))
            guard let self, !Task.isCancelled else { return }
            self.sendTask = nil
            self.flush()
        }
    }

    private func flush() {
        guard settings.discordEnabled, let ipc, ipc.connected else { return }
        let activity = build()
        let json = activity.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: .sortedKeys) }
            .map { String(decoding: $0, as: UTF8.self) } ?? "null"
        guard json != lastSent else { return }
        lastSent = json
        lastSendAt = Date()
        // A local socket write of under a kilobyte: done in place.
        do { try ipc.setActivity(activity) } catch { log.error("SET_ACTIVITY failed: \(error.localizedDescription)") }
    }

    /// The current lyric line's text, or nil between lines and without synced lyrics.
    private var lyric: String? {
        guard case let .synced(lines) = model.lyrics, let i = model.lineIndex, lines.indices.contains(i) else { return nil }
        let text = lines[i].text.trimmingCharacters(in: .whitespaces)
        return text.isEmpty ? nil : clean(text)
    }

    /// The word filter (menu): every text Overtone sends to Discord passes through it.
    private func clean(_ s: String) -> String { censor(text: s, level: settings.discordFilter) }

    /// Paused or nothing playing: no activity, as Discord's own Spotify status does.
    func build() -> [String: Any]? {
        guard let info = model.info, info.playing else { return nil }
        let title = Self.fit(clean(info.title.isEmpty ? "Untitled" : info.title))
        let artist = Self.fit(info.artist.isEmpty ? title : clean(info.artist))
        let service = Self.service(info.bundleID)
        let icon = self.icon(service)
        let cover = covers[Self.coverKey(info)] ?? nil
        let album = info.album.isEmpty || info.album == info.title ? nil : clean(info.album)

        // The member list reads "Listening to …" from state (status_display_type 1)
        // or details (2). For the lyric, the lyric moves into state and the artist
        // takes its place on the card's third line; between lines it's the artist.
        let line = settings.discordStatusLine
        let lyric = self.lyric
        let lyricStatus = line == .lyric && lyric != nil
        var assets: [String: Any] = [
            // Shown under the artist on the card: the lyric playing right now.
            "large_text": Self.fit(lyricStatus ? artist : lyric ?? album ?? title),
        ]
        if let large = cover ?? (self.assets.contains("overtone") ? "overtone" : icon) { assets["large_image"] = large }
        if let icon {
            assets["small_image"] = icon
            assets["small_text"] = service.name
        }
        var activity: [String: Any] = [
            "type": 2, // Listening
            "status_display_type": line == .title ? 2 : 1,
            "details": title,
            "state": lyricStatus ? Self.fit(lyric!) : artist,
            "assets": assets,
        ]
        if let duration = info.durationMs, duration > 0, info.hasTimeline {
            // Start/end make Discord draw the progress bar and elapsed time, from
            // the live clock. Small corrections keep the previous start so they
            // don't cost an update.
            let now = Int64(Date().timeIntervalSince1970 * 1000)
            var start = now - Int64(model.positionMs())
            if abs(start - lastStart) < 1500 { start = lastStart }
            lastStart = start
            activity["timestamps"] = ["start": start, "end": start + Int64(duration)]
        }
        return activity
    }

    /// The uploaded art asset when the Discord app has it, otherwise the player's site icon by URL.
    private func icon(_ service: (key: String?, name: String, domain: String?)) -> String? {
        if let key = service.key, assets.contains(key) { return key }
        return service.domain.map { "https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url=https://\($0)&size=128" }
    }

    // MARK: Connection

    private func connect() {
        guard settings.discordEnabled, !connecting, ipc?.connected != true else { return }
        let appID = self.appID
        connecting = true
        loadAssets(appID)
        let ipc = DiscordIPC(clientID: appID)
        Task { [weak self] in
            let ok = await Task.detached { ipc.connect() }.value
            guard let self else { return }
            self.connecting = false
            guard ok, self.settings.discordEnabled else {
                ipc.close()
                if self.settings.discordEnabled { self.scheduleRetry() } // Discord not running yet
                return
            }
            ipc.onDisconnect = { [weak self] reason in
                Task { @MainActor in
                    guard let self, self.ipc === ipc else { return }
                    self.ipc = nil
                    self.lastSent = nil
                    if reason != "disposed", self.settings.discordEnabled { self.scheduleRetry() }
                }
            }
            self.ipc = ipc
            self.lastSent = nil
            self.lastSendAt = .distantPast
            self.schedule()
        }
    }

    private func scheduleRetry() {
        retryTask?.cancel()
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: Self.retry)
            guard !Task.isCancelled else { return }
            self?.connect()
        }
    }

    /// Reads the app's uploaded asset names (a public endpoint), so missing icons fall back to URLs.
    private func loadAssets(_ appID: String) {
        guard let url = URL(string: "https://discord.com/api/v10/oauth2/applications/\(appID)/assets") else { return }
        Task { [weak self] in
            guard let (data, _) = try? await URLSession.shared.data(from: url),
                  let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return }
            let names = Set(list.compactMap { ($0["name"] as? String)?.lowercased() })
            guard let self else { return }
            self.assets = names
            self.lastSent = nil
            self.schedule()
        }
    }

    // MARK: Helpers

    static func coverKey(_ i: NowPlayingInfo) -> String { "\(i.artist)\u{1}\(i.title)" }

    /// Discord wants 2–128 characters per text field.
    static func fit(_ s: String) -> String {
        var s = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.count > 128 { s = String(s.prefix(127)) + "…" }
        while s.count < 2 { s += "\u{200B}" }
        return s
    }

    /// The player's art-asset key (design/discord-assets), name and site, from its bundle ID.
    static func service(_ bundleID: String?) -> (key: String?, name: String, domain: String?) {
        let id = (bundleID ?? "").lowercased()
        let table: [(match: String, key: String?, name: String, domain: String?)] = [
            ("spotify", "spotify", "Spotify", "open.spotify.com"),
            ("cider", "applemusic", "Apple Music (Cider)", "music.apple.com"),
            ("com.apple.music", "applemusic", "Apple Music", "music.apple.com"),
            ("com.apple.itunes", "applemusic", "iTunes", "music.apple.com"),
            ("tidal", "tidal", "TIDAL", "tidal.com"),
            ("deezer", "deezer", "Deezer", "deezer.com"),
            ("soundcloud", "soundcloud", "SoundCloud", "soundcloud.com"),
            ("amazon", "amazonmusic", "Amazon Music", "music.amazon.com"),
            ("youtube", "youtubemusic", "YouTube Music", "music.youtube.com"),
            ("ytmdesktop", "youtubemusic", "YouTube Music", "music.youtube.com"),
            ("qobuz", "qobuz", "Qobuz", "qobuz.com"),
            ("com.google.chrome", "chrome", "Google Chrome", "www.google.com/chrome"),
            ("com.microsoft.edge", "edge", "Microsoft Edge", "www.microsoft.com/edge"),
            ("firefox", "firefox", "Firefox", "www.firefox.com"),
            ("opera", "opera", "Opera", "www.opera.com"),
            ("brave", "brave", "Brave", "brave.com"),
            ("com.apple.safari", nil, "Safari", "www.apple.com/safari"),
            ("company.thebrowser", nil, "Arc", "arc.net"),
            ("vlc", "vlc", "VLC", "www.videolan.org"),
            ("mpv", "mpv", "mpv", "mpv.io"),
        ]
        if let hit = table.first(where: { id.contains($0.match) }) { return (hit.key, hit.name, hit.domain) }
        // Unknown player: no icon, but name it from the app itself.
        if let bundleID, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) {
            return (nil, FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: ""), nil)
        }
        return (nil, "a media player", nil)
    }
}
