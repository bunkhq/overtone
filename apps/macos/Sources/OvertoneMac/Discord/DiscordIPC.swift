import Darwin
import Foundation
import os

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "discord")

/// Minimal Discord RPC client over the desktop app's local socket
/// (`$TMPDIR/discord-ipc-N` on macOS). Frames are an 8-byte header (opcode and
/// length, little-endian int32) plus UTF-8 JSON. Only what Rich Presence needs:
/// handshake, SET_ACTIVITY, ping/pong. Mirrors the Windows DiscordIpc.
final class DiscordIPC: @unchecked Sendable {
    private enum Op: Int32 { case handshake = 0, frame = 1, close = 2, ping = 3, pong = 4 }

    private let clientID: String
    private let lock = NSLock()
    private var fd: Int32 = -1
    /// Called once, from the reader thread, when Discord closes or rejects the connection.
    var onDisconnect: (@Sendable (String) -> Void)?
    /// Called from the reader thread with every reply frame (for SET_ACTIVITY: the accepted activity).
    var onReply: (@Sendable ([String: Any]) -> Void)?

    private let paths: [String]?

    /// `paths` overrides where to look for Discord's socket (tests).
    init(clientID: String, paths: [String]? = nil) {
        self.clientID = clientID
        self.paths = paths
    }

    var connected: Bool { lock.withLock { fd >= 0 } }

    /// Where Discord (and its Canary/PTB builds) listen. Blocking: call off the main thread.
    static func socketPaths() -> [String] {
        let env = ProcessInfo.processInfo.environment
        // Development: point at a fake Discord to inspect what would be sent.
        if let override = env["OVERTONE_DISCORD_SOCKET"] { return [override] }
        var bases = ["XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"].compactMap { env[$0] }
        bases.append(NSTemporaryDirectory())
        bases.append("/tmp")
        var seen = Set<String>()
        return bases.flatMap { base in (0..<10).map { (base as NSString).appendingPathComponent("discord-ipc-\($0)") } }
            .filter { seen.insert($0).inserted }
    }

    /// Connects to the first running Discord client and completes the handshake.
    func connect() -> Bool {
        for path in paths ?? Self.socketPaths() where FileManager.default.fileExists(atPath: path) {
            let s = socket(AF_UNIX, SOCK_STREAM, 0)
            guard s >= 0 else { continue }
            var on: Int32 = 1
            setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            var addr = sockaddr_un()
            addr.sun_family = sa_family_t(AF_UNIX)
            let bytes = Array(path.utf8)
            guard bytes.count < MemoryLayout.size(ofValue: addr.sun_path) else { Darwin.close(s); continue }
            withUnsafeMutableBytes(of: &addr.sun_path) { buf in
                buf.copyBytes(from: bytes)
                buf[bytes.count] = 0
            }
            let ok = withUnsafePointer(to: &addr) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(s, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
            }
            guard ok == 0 else { Darwin.close(s); continue }
            lock.withLock { fd = s }
            do {
                try write(.handshake, ["v": 1, "client_id": clientID])
                let (op, body) = try read(s)
                guard op == Op.frame.rawValue, body["evt"] as? String == "READY" else {
                    // An unknown application ID comes back as a close frame with the reason.
                    drop("handshake rejected: \(body)")
                    return false
                }
            } catch {
                drop("handshake failed: \(error)")
                return false
            }
            let thread = Thread { [weak self] in self?.readLoop(s) }
            thread.name = "overtone.discord.read"
            thread.start()
            log.info("connected to Discord at \(path, privacy: .public)")
            return true
        }
        return false
    }

    /// Sets (or with nil, clears) this process's activity.
    func setActivity(_ activity: [String: Any]?) throws {
        try write(.frame, [
            "cmd": "SET_ACTIVITY",
            "args": ["pid": Int(getpid()), "activity": activity ?? NSNull()] as [String: Any],
            "nonce": UUID().uuidString,
        ])
    }

    func close() { drop("disposed") }

    private func write(_ op: Op, _ payload: [String: Any]) throws {
        let body = try JSONSerialization.data(withJSONObject: payload)
        var frame = Data(capacity: 8 + body.count)
        withUnsafeBytes(of: op.rawValue.littleEndian) { frame.append(contentsOf: $0) }
        withUnsafeBytes(of: Int32(body.count).littleEndian) { frame.append(contentsOf: $0) }
        frame.append(body)
        try lock.withLock {
            guard fd >= 0 else { throw POSIXError(.ENOTCONN) }
            try frame.withUnsafeBytes { buf in
                var sent = 0
                while sent < buf.count {
                    let n = Darwin.write(fd, buf.baseAddress! + sent, buf.count - sent)
                    if n <= 0 { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
                    sent += n
                }
            }
        }
    }

    private func read(_ s: Int32) throws -> (Int32, [String: Any]) {
        func exactly(_ count: Int) throws -> Data {
            var data = Data(count: count)
            var got = 0
            try data.withUnsafeMutableBytes { buf in
                while got < count {
                    let n = Darwin.read(s, buf.baseAddress! + got, count - got)
                    if n <= 0 { throw POSIXError(.ECONNRESET) }
                    got += n
                }
            }
            return data
        }
        let header = try exactly(8)
        let op = header.withUnsafeBytes { Int32(littleEndian: $0.loadUnaligned(fromByteOffset: 0, as: Int32.self)) }
        let len = header.withUnsafeBytes { Int32(littleEndian: $0.loadUnaligned(fromByteOffset: 4, as: Int32.self)) }
        guard (0...(1 << 20)).contains(len) else { throw POSIXError(.EBADMSG) }
        let body = len == 0 ? Data() : try exactly(Int(len))
        let json = (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] ?? [:]
        return (op, json)
    }

    /// Drains Discord's replies (the socket stalls if nobody reads) and answers pings.
    private func readLoop(_ s: Int32) {
        while true {
            guard let (op, body) = try? read(s) else {
                drop("connection closed")
                return
            }
            switch op {
            case Op.ping.rawValue:
                try? write(.pong, body)
            case Op.close.rawValue:
                drop("closed by Discord: \(body)")
                return
            default:
                if body["evt"] as? String == "ERROR" { log.error("Discord RPC error: \(String(describing: body), privacy: .public)") }
                onReply?(body)
            }
        }
    }

    private func drop(_ reason: String) {
        let s: Int32 = lock.withLock {
            let s = fd
            fd = -1
            return s
        }
        guard s >= 0 else { return }
        Darwin.close(s)
        log.info("Discord connection dropped: \(reason, privacy: .public)")
        onDisconnect?(reason)
    }
}
