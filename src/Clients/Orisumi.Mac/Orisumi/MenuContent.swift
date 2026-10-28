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
                    Text("\(observer.name)：\(ObserverText.state(observer))")
                }
                .disabled(model.busy.contains(observer.packageId))
            }
        }
        let pending = model.observers.reduce(0) { $0 + $1.delivery.pending }
        let saved = model.observers.reduce(0) { $0 + $1.delivery.saved }
        if !model.observers.isEmpty {
            Text("待发送 \(pending)，已保存 \(saved)")
        }
        ForEach(model.observers.filter { $0.delivery.rejection != nil }) { observer in
            Text("\(observer.name) 的发送已暂停：\(observer.delivery.rejection!.rule)")
        }
        Divider()
        Button("打开 Orisumi") { open(nil) }
        Button("打开 Web 控制台") { openURL(Preferences.webConsole) }
        Button("设置…") { open(.settings) }
            .keyboardShortcut(",")
        Divider()
        Button("退出 Orisumi") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    private func open(_ item: Navigation.Item?) {
        if let item { navigation.selection = item }
        openWindow(id: MainView.windowId)
        NSApp.activate()
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

    static func delivery(_ delivery: Delivery) -> String {
        "草稿 \(delivery.drafts) · 待发送 \(delivery.pending) · 已保存 \(delivery.saved)"
    }

    static func hasFailure(_ model: RuntimeModel) -> Bool {
        if case .failed = model.state { return true }
        return model.observers.contains { $0.state == .failed || $0.delivery.rejection != nil || $0.delivery.error != nil }
    }
}
