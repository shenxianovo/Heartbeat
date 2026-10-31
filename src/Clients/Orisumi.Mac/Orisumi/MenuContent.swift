import AppKit
import OrisumiClient
import SwiftUI

/// The mascot in the menu bar: grey while nothing collects, with a red dot after a failure.
@MainActor
enum MenuBarIcon {
    static func image(for model: RuntimeModel) -> NSImage {
        let collecting = model.state == .running && model.installed.contains { $0.state == .running }
        let base = NSImage(named: collecting ? "MenuBarIcon" : "MenuBarIconPaused")!
        guard ObserverText.hasFailure(model) else { return base }
        return NSImage(size: base.size, flipped: false) { rect in
            base.draw(in: rect)
            let dot = NSRect(x: rect.maxX - 8, y: rect.minY, width: 8, height: 8)
            NSColor.white.setFill()
            NSBezierPath(ovalIn: dot).fill()
            NSColor.systemRed.setFill()
            NSBezierPath(ovalIn: dot.insetBy(dx: 1, dy: 1)).fill()
            return true
        }
    }
}

struct MenuContent: View {
    let model: RuntimeModel
    let navigation: Navigation
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openURL) private var openURL
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        Text(ObserverText.runtime(model))
        if case .failed = model.state {
            Button("重新启动 Runtime") { model.retryRuntime() }
        }
        if !model.installed.isEmpty {
            Divider()
            ForEach(model.installed) { observer in
                Toggle(isOn: Binding(get: { observer.enabled },
                                     set: { enabled in Task { await model.setEnabled(observer.packageId, enabled) } })) {
                    Text(label(observer))
                }
                .disabled(model.busy.contains(observer.packageId))
            }
        }
        Divider()
        Button("打开 Orisumi") {
            openWindow(id: MainView.windowId)
            NSApp.activate()
        }
        Button("打开 Web 控制台") { openURL(Preferences.webConsole) }
        Button("设置…") { SettingsOpener(navigation: navigation, openSettings: openSettings)(navigation.settingsTab) }
            .keyboardShortcut(",")
        Divider()
        Button("退出 Orisumi") { AppDelegate.quit() }
            .keyboardShortcut("q")
    }

    /// The checkmark already says whether it is enabled; the state is added only when it is not the expected one.
    private func label(_ observer: ObserverStatus) -> String {
        switch observer.state {
        case .running where observer.enabled, .stopped where !observer.enabled: observer.name
        default: "\(observer.name)（\(ObserverText.state(observer))）"
        }
    }
}

/// How states read in the menu and the window.
@MainActor
enum ObserverText {
    static func runtime(_ model: RuntimeModel) -> String {
        switch model.state {
        case .starting: return "正在启动"
        case .restarting: return "Runtime 意外退出，正在重新启动"
        case .failed(let message): return "Runtime 已停止：\(message.prefix(80))"
        case .stopped: return "Runtime 未运行"
        case .running:
            let running = model.installed.filter { $0.state == .running }.count
            return running == 0 ? "没有正在采集的 Observer" : "\(running) 个 Observer 正在采集"
        }
    }

    /// The name of the Observer's view in the window: what it records when the client has a view for it.
    static func viewName(_ observer: ObserverStatus) -> String {
        observer.packageId == ForegroundToday.packageId ? "前台应用" : observer.name
    }

    static func state(_ observer: ObserverStatus) -> String {
        guard observer.installed else { return "已卸载" }
        switch observer.state {
        case .starting: return "正在启动"
        case .running: return "正在采集"
        case .stopping: return "正在停止"
        case .waiting: return "意外退出，等待重启"
        case .failed: return "已停止，需要重试"
        case .stopped: return observer.enabled ? "未运行" : "已停用"
        }
    }

    static func color(_ observer: ObserverStatus) -> Color {
        guard observer.installed else { return .secondary }
        switch observer.state {
        case .running: return .green
        case .starting, .stopping, .waiting: return .orange
        case .failed: return .red
        case .stopped: return .secondary
        }
    }

    /// The Observer's process failure, if any.
    static func problems(_ observer: ObserverStatus) -> [String] {
        observer.error.map { [$0] } ?? []
    }

    /// Forwarded requests still waiting and writes the backend rejected; nil when there are neither.
    static func writes(_ observer: ObserverStatus) -> String? {
        var parts: [String] = []
        if observer.writes.pending > 0 { parts.append("\(observer.writes.pending) 个请求等待转发") }
        if observer.writes.rejected > 0 { parts.append("\(observer.writes.rejected) 条被拒绝") }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    static func hasFailure(_ model: RuntimeModel) -> Bool {
        if case .failed = model.state { return true }
        return model.observers.contains { $0.state == .failed }
    }
}
