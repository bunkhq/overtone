import AppKit
import OvertoneMac

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    // Menu bar only, also when run outside the bundle (`swift run`), where LSUIElement isn't read.
    app.setActivationPolicy(.accessory)
    app.run()
}
