import AppKit
import CryptoKit
import os

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "update")

/// Checks GitHub Releases for a newer Overtone (shortly after launch, then every six
/// hours, or from the menu) and installs it in one click: downloads the disk image,
/// checks it against the release's SHA256SUMS, copies the app out of it, quits, and
/// a small script swaps the new app in place of this one and opens it. A copy that
/// can't be replaced (not in a folder this user can write, or run from the disk
/// image) opens the release page instead. Same flow as the Windows tray's updater.
@MainActor
final class Updater {
    struct Release: Sendable, Equatable {
        let version: String
        let page: URL
        let dmgName: String?
        let dmg: URL?
        let sums: URL?
    }

    static let repo = "bunkhq/overtone"
    private static let firstCheck: TimeInterval = 20
    private static let interval: TimeInterval = 6 * 60 * 60

    /// The newer release found by the last check, or nil.
    private(set) var available: Release?
    /// True while downloading and unpacking the update.
    private(set) var installing = false
    private var checking = false
    private var timer: Timer?

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    }

    /// Whether Install replaces this copy, or only opens the release page.
    static var canInstallInPlace: Bool {
        let app = Bundle.main.bundleURL
        guard app.pathExtension == "app" else { return false } // swift run
        return FileManager.default.isWritableFile(atPath: app.deletingLastPathComponent().path)
    }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: Self.firstCheck, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                Task { await self.check(manual: false) }
                self.timer = Timer.scheduledTimer(withTimeInterval: Self.interval, repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self else { return }
                        Task { await self.check(manual: false) }
                    }
                }
            }
        }
    }

    /// Asks GitHub for the latest release. A manual check reports "up to date" and errors too.
    func check(manual: Bool) async {
        guard !checking, !installing else { return }
        checking = true
        defer { checking = false }
        do {
            let latest = try await Self.fetchLatest()
            available = latest.flatMap { AppVersion.compare($0.version, Self.currentVersion) > 0 ? $0 : nil }
            if manual, available == nil {
                Self.alert("Overtone is up to date", "You have the latest version, \(Self.currentVersion).")
            }
        } catch {
            log.error("update check: \(error.localizedDescription)")
            if manual { Self.alert("Couldn't check for updates", "Check your connection and try again.") }
        }
    }

    /// Installs `available`, then quits; the swap script opens the new version.
    func install() async {
        guard let release = available, !installing else { return }
        guard Self.canInstallInPlace, let dmg = release.dmg, let dmgName = release.dmgName else {
            NSWorkspace.shared.open(release.page)
            return
        }
        installing = true
        do {
            guard let sums = release.sums else { throw UpdateError("\(release.version) has no SHA256SUMS") }
            let staged = try await Self.stage(dmg: dmg, named: dmgName, sums: sums)
            try Self.launchSwap(newApp: staged, replacing: Bundle.main.bundleURL)
            NSApp.terminate(nil)
        } catch {
            log.error("update install: \(error.localizedDescription)")
            installing = false
            Self.alert("Couldn't install the update", "Opening the download page instead.")
            NSWorkspace.shared.open(release.page)
        }
    }

    // MARK: Steps

    private static func fetchLatest() async throws -> Release? {
        // /releases/latest skips drafts and pre-releases.
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("Overtone/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        try ensureOK(response)
        return try parse(data)
    }

    nonisolated struct Payload: Decodable {
        struct Asset: Decodable {
            let name: String
            let browser_download_url: URL
        }
        let tag_name: String
        let html_url: URL
        let assets: [Asset]
    }

    nonisolated static func parse(_ data: Data) throws -> Release? {
        let p = try JSONDecoder().decode(Payload.self, from: data)
        let version = p.tag_name.hasPrefix("v") ? String(p.tag_name.dropFirst()) : p.tag_name
        guard !version.isEmpty else { return nil }
        let dmg = p.assets.first { $0.name.hasSuffix("-macOS.dmg") }
        let sums = p.assets.first { $0.name == "SHA256SUMS" }
        return Release(version: version, page: p.html_url, dmgName: dmg?.name, dmg: dmg?.browser_download_url, sums: sums?.browser_download_url)
    }

    /// Downloads and verifies the disk image, and copies the app out of it. Returns the staged app.
    private static func stage(dmg: URL, named name: String, sums: URL) async throws -> URL {
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("Overtone-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)

        let (downloaded, response) = try await URLSession.shared.download(from: dmg)
        try ensureOK(response)
        let image = work.appendingPathComponent(name)
        try FileManager.default.moveItem(at: downloaded, to: image)

        let (sumsData, sumsResponse) = try await URLSession.shared.data(from: sums)
        try ensureOK(sumsResponse)
        let hash = try await Task.detached { try sha256(of: image) }.value
        guard matches(String(decoding: sumsData, as: UTF8.self), name: name, hash: hash) else {
            throw UpdateError("\(name) does not match SHA256SUMS")
        }

        let mount = work.appendingPathComponent("mount")
        let staged = work.appendingPathComponent("Overtone.app")
        try await run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, image.path])
        do {
            try await run("/usr/bin/ditto", [mount.appendingPathComponent("Overtone.app").path, staged.path])
            try? await run("/usr/bin/hdiutil", ["detach", mount.path, "-force"])
        } catch {
            try? await run("/usr/bin/hdiutil", ["detach", mount.path, "-force"])
            throw error
        }
        try? FileManager.default.removeItem(at: image)

        // The right app, intact (its signature, ad hoc or not, covers every file).
        guard Bundle(url: staged)?.bundleIdentifier == Bundle.main.bundleIdentifier else {
            throw UpdateError("the disk image holds a different app")
        }
        try await run("/usr/bin/codesign", ["--verify", "--strict", staged.path])
        return staged
    }

    /// Starts a script that waits for this process to exit, swaps `newApp` in at
    /// `dest` (restoring the old app if the copy fails) and opens it.
    private static func launchSwap(newApp: URL, replacing dest: URL) throws {
        let script = #"""
        pid="$1"; new="$2"; dest="$3"; work="$4"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.2; done
        old="$dest.previous"
        rm -rf "$old"
        if mv "$dest" "$old"; then
          if ditto "$new" "$dest"; then rm -rf "$old"; else rm -rf "$dest"; mv "$old" "$dest"; fi
        fi
        open "$dest"
        rm -rf "$work"
        """#
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script, "overtone-update", String(getpid()), newApp.path, dest.path, newApp.deletingLastPathComponent().path]
        try p.run()
    }

    // MARK: Helpers

    /// True when SHA256SUMS (sha256sum format: "hash  name" per line) lists `name` with `hash`.
    nonisolated static func matches(_ sums: String, name: String, hash: String) -> Bool {
        sums.split(whereSeparator: \.isNewline).contains { line in
            let parts = line.split(maxSplits: 1, whereSeparator: \.isWhitespace)
            guard parts.count == 2 else { return false }
            var file = parts[1].trimmingCharacters(in: .whitespaces)
            if file.hasPrefix("*") { file.removeFirst() }
            return file == name && parts[0].lowercased() == hash.lowercased()
        }
    }

    private nonisolated static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private nonisolated static func run(_ tool: String, _ args: [String]) async throws {
        try await withCheckedThrowingContinuation { (done: CheckedContinuation<Void, Error>) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: tool)
            p.arguments = args
            p.standardOutput = FileHandle.nullDevice
            p.standardError = FileHandle.nullDevice
            p.terminationHandler = { p in
                p.terminationStatus == 0
                    ? done.resume()
                    : done.resume(throwing: UpdateError("\((tool as NSString).lastPathComponent) failed (\(p.terminationStatus))"))
            }
            do { try p.run() } catch { done.resume(throwing: error) }
        }
    }

    private static func ensureOK(_ response: URLResponse) throws {
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw UpdateError("HTTP \(http.statusCode) from \(http.url?.host ?? "GitHub")")
        }
    }

    private static func alert(_ title: String, _ message: String) {
        NSApp.activate()
        let a = NSAlert()
        a.messageText = title
        a.informativeText = message
        a.runModal()
    }
}

struct UpdateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// Semantic-version ordering for release tags: 0.2.0 > 0.2.0-dev.5 > 0.1.9.
enum AppVersion {
    static func compare(_ a: String, _ b: String) -> Int {
        let (na, pa) = split(a)
        let (nb, pb) = split(b)
        for i in 0..<max(na.count, nb.count) {
            let x = i < na.count ? na[i] : 0
            let y = i < nb.count ? nb[i] : 0
            if x != y { return x < y ? -1 : 1 }
        }
        // A pre-release sorts before its release.
        switch (pa, pb) {
        case (nil, nil): return 0
        case (nil, _): return 1
        case (_, nil): return -1
        case let (x?, y?): return comparePre(x, y)
        }
    }

    private static func split(_ v: String) -> ([Int], String?) {
        var v = v.trimmingCharacters(in: .whitespaces)
        if v.hasPrefix("v") || v.hasPrefix("V") { v.removeFirst() }
        v = String(v.split(separator: "+", maxSplits: 1).first ?? "")
        let parts = v.split(separator: "-", maxSplits: 1)
        let nums = (parts.first ?? "").split(separator: ".").map { Int($0) ?? 0 }
        return (nums, parts.count > 1 ? String(parts[1]) : nil)
    }

    private static func comparePre(_ a: String, _ b: String) -> Int {
        let x = a.split(separator: "."), y = b.split(separator: ".")
        for (p, q) in zip(x, y) {
            switch (Int(p), Int(q)) {
            case let (m?, n?) where m != n: return m < n ? -1 : 1
            case (_?, nil): return -1
            case (nil, _?): return 1
            case (nil, nil) where p != q: return p < q ? -1 : 1
            default: continue
            }
        }
        return x.count == y.count ? 0 : (x.count < y.count ? -1 : 1)
    }
}
