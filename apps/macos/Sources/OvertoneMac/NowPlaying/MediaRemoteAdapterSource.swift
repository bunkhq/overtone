import Darwin
import Foundation
import os

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "adapter")

/// Where the bundled adapter lives.
public struct AdapterPaths: Sendable {
    public var script: URL
    public var framework: URL
    public var testClient: URL?

    /// Inside Overtone.app, or `apps/macos/build/adapter` for `swift run`
    /// (override with `OVERTONE_ADAPTER_DIR`).
    public static func locate() -> AdapterPaths? {
        let fm = FileManager.default
        let bundle = Bundle.main
        if let script = bundle.url(forResource: "mediaremote-adapter", withExtension: "pl"),
           let frameworks = bundle.privateFrameworksURL {
            let framework = frameworks.appendingPathComponent("MediaRemoteAdapter.framework")
            let helper = bundle.bundleURL.appendingPathComponent("Contents/Helpers/MediaRemoteAdapterTestClient")
            if fm.fileExists(atPath: framework.path) {
                return AdapterPaths(script: script, framework: framework, testClient: fm.isExecutableFile(atPath: helper.path) ? helper : nil)
            }
        }
        var candidates: [URL] = []
        if let env = ProcessInfo.processInfo.environment["OVERTONE_ADAPTER_DIR"] {
            candidates.append(URL(fileURLWithPath: env))
        }
        // .build/<config>/Overtone → apps/macos/build/adapter
        var dir = bundle.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent()
        for _ in 0..<5 {
            guard let d = dir else { break }
            candidates.append(d.appendingPathComponent("build/adapter"))
            dir = d.deletingLastPathComponent()
        }
        for c in candidates {
            let script = c.appendingPathComponent("mediaremote-adapter.pl")
            let framework = c.appendingPathComponent("MediaRemoteAdapter.framework")
            let helper = c.appendingPathComponent("MediaRemoteAdapterTestClient")
            if fm.fileExists(atPath: script.path), fm.fileExists(atPath: framework.path) {
                return AdapterPaths(script: script, framework: framework, testClient: fm.isExecutableFile(atPath: helper.path) ? helper : nil)
            }
        }
        return nil
    }

    func arguments(_ command: [String]) -> [String] {
        [script.path, framework.path] + (testClient.map { [$0.path] } ?? []) + command
    }
}

/// Reads macOS Now Playing through `mediaremote-adapter`, which runs under
/// `/usr/bin/perl` (a system binary still entitled to MediaRemote on 15.4+).
/// One long-lived `stream` process pushes changes; nothing is polled.
@MainActor
public final class MediaRemoteAdapterSource: NowPlayingSource {
    public var onUpdate: ((NowPlayingInfo?) -> Void)?
    public let name = "Now Playing"

    private let paths: AdapterPaths
    private var process: Process?
    private var stopping = false
    private var restartDelay: TimeInterval = 1
    private var startedAt = Date.distantPast

    /// Records the stream's PID so a later launch can stop one this instance
    /// left behind (the adapter doesn't notice its parent dying, and only hits
    /// SIGPIPE on its next write, which may be hours away).
    private let pidFile: URL?

    public init(paths: AdapterPaths, stateDir: URL?) {
        self.paths = paths
        pidFile = stateDir?.appendingPathComponent("adapter-stream.pid")
        reapOrphan()
    }

    private func reapOrphan() {
        guard let pidFile, let text = try? String(contentsOf: pidFile, encoding: .utf8), let pid = pid_t(text.trimmingCharacters(in: .whitespacesAndNewlines)) else { return }
        try? FileManager.default.removeItem(at: pidFile)
        // Only an orphan (re-parented to launchd) running /usr/bin/perl: never an unrelated process.
        var info = proc_bsdinfo()
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size, info.pbi_ppid == 1 else { return }
        var path = [UInt8](repeating: 0, count: Int(MAXPATHLEN))
        let len = proc_pidpath(pid, &path, UInt32(path.count))
        guard len > 0, String(decoding: path.prefix(Int(len)), as: UTF8.self) == "/usr/bin/perl" else { return }
        log.info("stopping orphaned adapter stream \(pid)")
        kill(pid, SIGTERM)
    }

    private func writePID(_ pid: pid_t?) {
        guard let pidFile else { return }
        if let pid {
            try? FileManager.default.createDirectory(at: pidFile.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? String(pid).write(to: pidFile, atomically: true, encoding: .utf8)
        } else {
            try? FileManager.default.removeItem(at: pidFile)
        }
    }

    /// Runs the adapter's self-test. `true` means the stream will work.
    public nonisolated static func selfTest(_ paths: AdapterPaths) async -> Bool {
        guard paths.testClient != nil else { return true } // can't test; assume it works
        return await withCheckedContinuation { cont in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
            p.arguments = paths.arguments(["test"])
            p.standardOutput = FileHandle.nullDevice
            p.standardError = FileHandle.nullDevice
            p.terminationHandler = { cont.resume(returning: $0.terminationStatus == 0) }
            do {
                try p.run()
            } catch {
                cont.resume(returning: false)
                return
            }
            DispatchQueue.global().asyncAfter(deadline: .now() + 8) {
                if p.isRunning { p.terminate() }
            }
        }
    }

    public func start() {
        stopping = false
        launch()
    }

    public func stop() {
        stopping = true
        process?.terminate()
        process = nil
        writePID(nil)
    }

    public func send(_ command: TransportCommand) {
        let args: [String]
        switch command {
        case .togglePlayPause: args = ["send", "2"]
        case .next: args = ["send", "4"]
        case .previous: args = ["send", "5"]
        case let .seek(ms): args = ["seek", String(ms * 1000)]
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        p.arguments = paths.arguments(args)
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { log.error("send failed: \(error.localizedDescription)") }
    }

    private func launch() {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        p.arguments = paths.arguments(["stream", "--micros"])
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice

        let reader = StreamReader { [weak self] infos in
            Task { @MainActor in
                guard let self else { return }
                for info in infos { self.onUpdate?(info) }
            }
        }
        out.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
            } else {
                reader.feed(data)
            }
        }
        p.terminationHandler = { [weak self] proc in
            let status = proc.terminationStatus
            Task { @MainActor in self?.processEnded(proc, status: status) }
        }
        do {
            try p.run()
            process = p
            writePID(p.processIdentifier)
            startedAt = Date()
            log.info("stream started")
        } catch {
            log.error("could not start adapter: \(error.localizedDescription)")
            scheduleRestart()
        }
    }

    private func processEnded(_ proc: Process, status: Int32) {
        guard proc === process else { return }
        process = nil
        writePID(nil)
        if stopping { return }
        log.error("stream exited with \(status); restarting")
        onUpdate?(nil)
        // A stream that ran for a while gets a fresh backoff.
        if Date().timeIntervalSince(startedAt) > 60 { restartDelay = 1 }
        scheduleRestart()
    }

    private func scheduleRestart() {
        let delay = restartDelay
        restartDelay = min(restartDelay * 2, 30)
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !self.stopping, self.process == nil else { return }
            self.launch()
        }
    }
}

/// Owns the parser on a private serial queue, off the main thread: artwork
/// arrives as base64 inside the JSON and can be a few hundred KB.
private final class StreamReader: @unchecked Sendable {
    private let queue = DispatchQueue(label: "overtone.adapter.parse")
    private let parser = AdapterStreamParser()
    private let deliver: @Sendable ([NowPlayingInfo?]) -> Void

    init(deliver: @escaping @Sendable ([NowPlayingInfo?]) -> Void) {
        self.deliver = deliver
    }

    func feed(_ data: Data) {
        queue.async { [self] in
            let infos = parser.feed(data)
            if !infos.isEmpty { deliver(infos) }
        }
    }
}
