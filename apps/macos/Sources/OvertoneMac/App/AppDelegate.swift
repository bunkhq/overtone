import AppKit
import Observation
import OvertoneCore
import os
import ServiceManagement

private let log = Logger(subsystem: "com.bunkhq.overtone", category: "app")

/// Menu bar app: no Dock icon, no main window. Owns the source, the model,
/// the overlay and the menu.
@MainActor
public final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let settings = Settings()
    private lazy var model = NowPlayingModel(lyricsCacheDir: Self.cacheDir?.appendingPathComponent("lyrics"))
    private var source: NowPlayingSource?
    private var overlay: OverlayController?
    private var statusItem: NSStatusItem?
    private var hotKeys: HotKeys?
    private var discord: DiscordPresence?
    private lazy var musicGate = MusicGate(settings: settings) { [weak self] in self?.model.ingest($0) }
    private var signalSources: [DispatchSourceSignal] = []

    override public init() {}

    public func applicationDidFinishLaunching(_ notification: Notification) {
        model.forcedTone = settings.tone.forced(systemDark: Self.systemDark)
        overlay = OverlayController(model: model, settings: settings) { [weak self] in self?.source?.send($0) }
        discord = DiscordPresence(model: model, settings: settings)
        setUpStatusItem()
        updateMenuBarIcon()
        setUpHotKeys()
        observeTone()
        handleTerminationSignals()
        startSource()
    }

    /// `kill`, logout and Ctrl-C under `swift run` send signals that skip
    /// applicationWillTerminate; route them through it so the adapter stops too.
    private func handleTerminationSignals() {
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            signal(sig, SIG_IGN)
            let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            src.setEventHandler { NSApp.terminate(nil) }
            src.resume()
            signalSources.append(src)
        }
    }

    public func applicationWillTerminate(_ notification: Notification) {
        discord?.shutdown()
        source?.stop()
    }

    // MARK: Source

    private func startSource() {
        guard let paths = AdapterPaths.locate() else {
            log.error("mediaremote-adapter not found; using Spotify/Music notifications")
            use(PlayerNotificationSource())
            return
        }
        Task {
            if await MediaRemoteAdapterSource.selfTest(paths) {
                use(MediaRemoteAdapterSource(paths: paths, stateDir: Self.cacheDir))
            } else {
                log.error("mediaremote-adapter self-test failed; using Spotify/Music notifications")
                use(PlayerNotificationSource())
            }
        }
    }

    private func use(_ s: NowPlayingSource) {
        source?.stop()
        source = s
        s.onUpdate = { [weak self] in self?.musicGate.ingest($0) }
        s.start()
        log.info("source: \(s.name)")
    }

    private static var cacheDir: URL? {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first?
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "com.bunkhq.overtone")
    }

    private var appearanceObservation: NSKeyValueObservation?

    private static var systemDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    private func observeTone() {
        if appearanceObservation == nil {
            // "Match System" follows light/dark switches (including automatic ones) live.
            appearanceObservation = NSApp.observe(\.effectiveAppearance) { [weak self] _, _ in
                Task { @MainActor in
                    guard let self else { return }
                    self.model.forcedTone = self.settings.tone.forced(systemDark: Self.systemDark)
                }
            }
        }
        withObservationTracking {
            model.forcedTone = settings.tone.forced(systemDark: Self.systemDark)
        } onChange: { [weak self] in
            Task { @MainActor in self?.observeTone() }
        }
    }

    // MARK: Hotkeys

    private func setUpHotKeys() {
        let keys = HotKeys()
        keys.register(HotKeys.toggleOverlay) { [weak self] in self?.settings.overlayVisible.toggle() }
        keys.register(HotKeys.ghost) { [weak self] in self?.settings.ghost.toggle() }
        keys.register(HotKeys.cycleLayout) { [weak self] in self?.settings.cycleLayout() }
        hotKeys = keys
    }

    // MARK: Menu bar

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = MenuBarIcon.make()
        item.button?.setAccessibilityLabel("Overtone")
        // The coloured icon re-picks its lightness when the menu bar turns light or dark.
        menuBarAppearance = item.button?.observe(\.effectiveAppearance) { [weak self] _, _ in
            Task { @MainActor in self?.updateMenuBarIcon() }
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        statusItem = item
    }

    private var menuBarAppearance: NSKeyValueObservation?

    /// Monochrome template by default (as the menu bar expects); opt in to the
    /// waves in the track's colours, like the Windows tray icon. No artwork: the template.
    private func updateMenuBarIcon() {
        withObservationTracking {
            let palette = settings.colorMenuBarIcon ? model.artworkPalette : nil
            let darkBar = statusItem?.button?.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            let colors = palette.map { trayColors(palette: $0, darkBar: darkBar).map { OKLCH(rgb: $0).nsColor } }
            statusItem?.button?.image = MenuBarIcon.make(colors: colors)
        } onChange: { [weak self] in
            Task { @MainActor in self?.updateMenuBarIcon() }
        }
    }

    public func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let info = model.info

        if let info {
            let header = NSMenuItem(title: info.title, action: nil, keyEquivalent: "")
            header.attributedTitle = NSAttributedString(string: info.title, attributes: [.font: NSFont.menuFont(ofSize: 0).bold])
            header.isEnabled = false
            menu.addItem(header)
            let sub = [info.artist, Self.appName(info.bundleID)].compactMap { $0?.isEmpty == false ? $0 : nil }
            if !sub.isEmpty {
                let line = NSMenuItem(title: sub.joined(separator: " · "), action: nil, keyEquivalent: "")
                line.isEnabled = false
                menu.addItem(line)
            }
        } else {
            let none = NSMenuItem(title: "Nothing playing", action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
        }
        menu.addItem(item(info?.playing == true ? "Pause" : "Play", enabled: info != nil) { [weak self] in self?.source?.send(.togglePlayPause) })
        menu.addItem(item("Next Track", enabled: info != nil) { [weak self] in self?.source?.send(.next) })
        menu.addItem(item("Previous Track", enabled: info != nil) { [weak self] in self?.source?.send(.previous) })
        menu.addItem(.separator())

        menu.addItem(item("Show Overlay", checked: settings.overlayVisible, shortcut: HotKeys.toggleOverlay) { [weak self] in
            self?.settings.overlayVisible.toggle()
        })
        let layouts = NSMenu()
        for l in OverlayLayout.allCases {
            layouts.addItem(item(l.title, checked: settings.layout == l) { [weak self] in self?.settings.applyPreset(l) })
        }
        layouts.addItem(.separator())
        layouts.addItem(item("Next Layout", shortcut: HotKeys.cycleLayout) { [weak self] in self?.settings.cycleLayout() })
        menu.addItem(submenu("Layout", layouts))
        menu.addItem(item("Ghost Mode", checked: settings.ghost, shortcut: HotKeys.ghost) { [weak self] in self?.settings.ghost.toggle() })
        let tones = NSMenu()
        for t in TonePreference.allCases {
            tones.addItem(item(t.title, checked: settings.tone == t) { [weak self] in self?.settings.tone = t })
        }
        menu.addItem(submenu("Colours", tones))
        let backgrounds = NSMenu()
        for b in OverlayBackground.available {
            backgrounds.addItem(item(b.title, checked: settings.background == b) { [weak self] in self?.settings.background = b })
        }
        menu.addItem(submenu("Background", backgrounds))
        menu.addItem(item("Coloured Menu Bar Icon", checked: settings.colorMenuBarIcon) { [weak self] in self?.settings.colorMenuBarIcon.toggle() })
        menu.addItem(item("Music Only (Ignore Videos)", checked: settings.musicOnly) { [weak self] in
            guard let self else { return }
            self.settings.musicOnly.toggle()
            self.musicGate.reevaluate()
        })
        menu.addItem(.separator())

        menu.addItem(item("Show in Discord Status", checked: settings.discordEnabled) { [weak self] in
            guard let self else { return }
            self.discord?.setEnabled(!self.settings.discordEnabled)
        })
        let statusLines = NSMenu()
        for l in DiscordStatusLine.allCases {
            statusLines.addItem(item(l.title, checked: settings.discordStatusLine == l) { [weak self] in self?.settings.discordStatusLine = l })
        }
        menu.addItem(submenu("Discord Status Shows", statusLines))
        let filters = NSMenu()
        for f in CensorLevel.ordered {
            filters.addItem(item(f.title, checked: settings.discordFilter == f) { [weak self] in self?.settings.discordFilter = f })
        }
        menu.addItem(submenu("Discord Word Filter", filters))
        menu.addItem(.separator())

        let canLogin = Bundle.main.bundleURL.pathExtension == "app"
        menu.addItem(item("Open at Login", enabled: canLogin, checked: canLogin && SMAppService.mainApp.status == .enabled) {
            Self.toggleLoginItem()
        })
        menu.addItem(item("About Overtone") {
            NSApp.activate()
            NSApp.orderFrontStandardAboutPanel(nil)
        })
        menu.addItem(item("Quit Overtone", key: "q") { NSApp.terminate(nil) })
    }

    private static func toggleLoginItem() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            log.error("login item: \(error.localizedDescription)")
        }
    }

    private static func appName(_ bundleID: String?) -> String? {
        guard let bundleID, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return nil }
        return FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
    }

    private func item(
        _ title: String, enabled: Bool = true, checked: Bool = false, key: String = "",
        shortcut: HotKeys.Shortcut? = nil, action: @escaping () -> Void
    ) -> NSMenuItem {
        let i = ClosureMenuItem(title: title, keyEquivalent: shortcut?.key ?? key, handler: action)
        if let shortcut { i.keyEquivalentModifierMask = shortcut.modifiers }
        i.isEnabled = enabled
        i.state = checked ? .on : .off
        return i
    }

    private func submenu(_ title: String, _ menu: NSMenu) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.submenu = menu
        return i
    }
}

private final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, keyEquivalent: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(fire), keyEquivalent: keyEquivalent)
        target = self
    }

    @available(*, unavailable)
    required init(coder: NSCoder) { fatalError() }

    @objc private func fire() { handler() }
}

private extension NSFont {
    var bold: NSFont { NSFontManager.shared.convert(self, toHaveTrait: .boldFontMask) }
}
