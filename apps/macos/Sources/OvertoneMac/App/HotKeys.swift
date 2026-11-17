import AppKit
import Carbon.HIToolbox

/// System-wide shortcuts via Carbon's RegisterEventHotKey: the one global
/// hotkey API that needs no Accessibility permission.
@MainActor
final class HotKeys {
    struct Shortcut {
        let keyCode: Int
        let modifiers: NSEvent.ModifierFlags
        let key: String // for menu display

        var carbonModifiers: UInt32 {
            var m: UInt32 = 0
            if modifiers.contains(.command) { m |= UInt32(cmdKey) }
            if modifiers.contains(.option) { m |= UInt32(optionKey) }
            if modifiers.contains(.control) { m |= UInt32(controlKey) }
            if modifiers.contains(.shift) { m |= UInt32(shiftKey) }
            return m
        }
    }

    static let toggleOverlay = Shortcut(keyCode: kVK_ANSI_O, modifiers: [.control, .option, .command], key: "o")
    static let ghost = Shortcut(keyCode: kVK_ANSI_G, modifiers: [.control, .option, .command], key: "g")
    static let cycleLayout = Shortcut(keyCode: kVK_ANSI_L, modifiers: [.control, .option, .command], key: "l")

    private var refs: [EventHotKeyRef] = []
    private var handlers: [UInt32: () -> Void] = [:]
    private var handlerRef: EventHandlerRef?

    init() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        let me = Unmanaged.passUnretained(self).toOpaque()
        InstallEventHandler(GetApplicationEventTarget(), { _, event, userData in
            guard let event, let userData else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil, MemoryLayout<EventHotKeyID>.size, nil, &id)
            let hotKeys = Unmanaged<HotKeys>.fromOpaque(userData).takeUnretainedValue()
            MainActor.assumeIsolated { hotKeys.handlers[id.id]?() }
            return noErr
        }, 1, &spec, me, &handlerRef)
    }

    func register(_ shortcut: Shortcut, action: @escaping () -> Void) {
        let id = UInt32(handlers.count + 1)
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(UInt32(shortcut.keyCode), shortcut.carbonModifiers, EventHotKeyID(signature: OSType(0x4F56544E), id: id), GetApplicationEventTarget(), 0, &ref) // 'OVTN'
        guard status == noErr, let ref else { return } // taken by another app: the menu still works
        refs.append(ref)
        handlers[id] = action
    }
}
