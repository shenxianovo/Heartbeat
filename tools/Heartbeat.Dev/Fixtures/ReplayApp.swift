import AppKit

// A real, disposable foreground application. It supplies OS input, never Collector Records.
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let previous = NSWorkspace.shared.frontmostApplication
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 160),
                      styleMask: [.titled], backing: .buffered, defer: false)
window.title = "Heartbeat Replay Probe"
let label = NSTextField(labelWithString: "Heartbeat is verifying this application's path to Web replay.")
label.frame = NSRect(x: 20, y: 60, width: 480, height: 40)
label.maximumNumberOfLines = 2
window.contentView?.addSubview(label)
window.center()
window.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)

let ready = URL(fileURLWithPath: CommandLine.arguments[1])
var transitions: [[String: Any]] = []
var wasForeground: Bool?
let timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { timer in
    let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier
    if wasForeground != foreground {
        wasForeground = foreground
        transitions.append(["at": ISO8601DateFormatter().string(from: Date()), "foreground": foreground])
        let evidence = ready.deletingLastPathComponent().appendingPathComponent("foreground.json")
        try! JSONSerialization.data(withJSONObject: transitions).write(to: evidence, options: .atomic)
    }
    if foreground && !FileManager.default.fileExists(atPath: ready.path) {
        try! Data("ready".utf8).write(to: ready, options: .atomic)
    }
}
signal(SIGINT, SIG_IGN)
let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
interrupt.setEventHandler {
    if NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier {
        previous?.activate(options: [])
    }
    app.terminate(nil)
}
interrupt.resume()
app.run()
