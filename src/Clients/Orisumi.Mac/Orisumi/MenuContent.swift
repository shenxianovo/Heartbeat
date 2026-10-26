import AppKit
import OrisumiClient
import SwiftUI

/// The mascot in the menu bar: grey while paused, with a red dot after a failure.
@MainActor
enum MenuBarIcon {
    static func image(for state: ClientModel.State) -> NSImage {
        let base = NSImage(named: state == .paused ? "MenuBarIconPaused" : "MenuBarIcon")!
        guard case .failed = state else { return base }
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
    let model: ClientModel
    let navigation: Navigation
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openURL) private var openURL

    var body: some View {
        Text(StateText.title(model.state))
        if let checkpoint = model.checkpoint {
            Text("前台：\(checkpoint.name ?? "未知应用")，\(Format.time(checkpoint.startAt)) 起")
        }
        if let delivery = model.delivery {
            Text("待发送 \(delivery.pending)，已保存 \(delivery.saved)")
            if let rejection = delivery.rejection {
                Text("发送已暂停：\(rejection.rule) \(rejection.detail.prefix(80))")
            }
            if let error = delivery.error {
                Text("发送已暂停：\(error.prefix(80))")
            }
        }
        Divider()
        switch model.state {
        case .running: Button("暂停采集") { Task { await model.pause() } }
        case .paused: Button("继续采集") { model.start() }
        case .failed: Button("重新启动") { model.start() }
        }
        Button("打开 Orisumi") { open(.status) }
        Button("打开 Web 控制台") { openURL(Preferences.webConsole) }
        Button("设置…") { open(.settings) }
            .keyboardShortcut(",")
        Divider()
        Button("退出 Orisumi") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    private func open(_ page: Navigation.Page) {
        navigation.page = page
        openWindow(id: MainView.windowId)
        NSApp.activate()
    }
}

enum StateText {
    static func title(_ state: ClientModel.State) -> String {
        switch state {
        case .running: "正在采集"
        case .paused: "已暂停"
        case .failed(let message): "已停止：\(message.prefix(80))"
        }
    }
}
