import Foundation

/// Parses `mediaremote-adapter stream --micros` output: one JSON object per
/// line, `{"type":"data","diff":Bool,"payload":{...}}`. Diff payloads only
/// carry changed keys, with `null` for keys that vanished, so the parser keeps
/// the merged state. Not thread-safe; feed it from one queue.
public final class AdapterStreamParser {
    private var buffer = Data()
    private var state: [String: Any] = [:]

    public init() {}

    /// Feeds raw stdout bytes. Returns one state per complete line, oldest first.
    public func feed(_ chunk: Data) -> [NowPlayingInfo?] {
        buffer.append(chunk)
        var out: [NowPlayingInfo?] = []
        while let nl = buffer.firstIndex(of: 0x0A) {
            let line = buffer[buffer.startIndex..<nl]
            buffer.removeSubrange(buffer.startIndex...nl)
            if let info = apply(line: Data(line)) {
                out.append(info)
            }
        }
        return out
    }

    /// `nil` for lines that aren't data messages; `.some(nil)` when nothing is playing.
    func apply(line: Data) -> NowPlayingInfo?? {
        guard !line.isEmpty,
              let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              obj["type"] as? String == "data",
              let payload = obj["payload"] as? [String: Any]
        else { return nil }
        if obj["diff"] as? Bool == true {
            for (k, v) in payload {
                if v is NSNull { state.removeValue(forKey: k) } else { state[k] = v }
            }
        } else {
            state = payload
        }
        return .some(Self.info(from: state))
    }

    static func info(from d: [String: Any]) -> NowPlayingInfo? {
        guard let title = d["title"] as? String, !title.isEmpty else { return nil }
        func micros(_ key: String) -> UInt64? {
            guard let n = d[key] as? NSNumber, n.doubleValue >= 0 else { return nil }
            return UInt64(n.doubleValue / 1000)
        }
        let playing = (d["playing"] as? Bool) ?? false
        var rate = (d["playbackRate"] as? NSNumber)?.doubleValue ?? 1
        if !(rate > 0) { rate = 1 }
        let reportedAt = (d["timestampEpochMicros"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue / 1_000_000) }
        let artwork = (d["artworkData"] as? String).flatMap { Data(base64Encoded: $0, options: .ignoreUnknownCharacters) }
        return NowPlayingInfo(
            bundleID: (d["parentApplicationBundleIdentifier"] as? String) ?? (d["bundleIdentifier"] as? String),
            title: title,
            artist: (d["artist"] as? String) ?? "",
            album: (d["album"] as? String) ?? "",
            durationMs: micros("durationMicros").flatMap { $0 > 0 ? $0 : nil },
            elapsedMs: micros("elapsedTimeMicros"),
            reportedAt: reportedAt,
            playing: playing,
            rate: rate,
            artwork: artwork,
            mediaType: d["mediaType"] as? String
        )
    }
}
