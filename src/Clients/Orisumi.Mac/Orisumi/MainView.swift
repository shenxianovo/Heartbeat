import AppKit
import OrisumiClient
import SwiftUI

/// The main window: the registered Observers in the sidebar, the selected one on the right.
/// The app shows in the Dock only while it is open.
struct MainView: View {
    static let windowId = "main"
    let model: RuntimeModel
    @Bindable var navigation: Navigation
    @State private var adding = false

    var body: some View {
        NavigationSplitView {
            Sidebar(model: model, selection: $navigation.selection, adding: $adding)
                .navigationSplitViewColumnWidth(min: 210, ideal: 230, max: 300)
        } detail: {
            switch navigation.selection {
            case .observer(let packageId):
                if let observer = model.observer(packageId) {
                    ObserverDetail(model: model, observer: observer)
                } else {
                    ContentUnavailableView("没有这个 Observer", systemImage: "questionmark.circle",
                                           description: Text("它已卸载，本机也没有待发送的数据。"))
                }
            case .settings:
                SettingsPage(model: model)
            case nil:
                ContentUnavailableView {
                    Label("还没有 Observer", systemImage: "eye")
                } description: {
                    Text("安装一个 Observer，它会直接开始记录。")
                } actions: {
                    Button("添加 Observer…") { adding = true }
                }
            }
        }
        .sheet(isPresented: $adding) { AddObserverSheet(model: model) }
        .frame(minWidth: 980, minHeight: 680)
        .onAppear {
            navigation.windowVisible = true
            selectFirst()
            Task {
                await model.refresh()
                await model.refreshCatalog()
            }
            NSApp.setActivationPolicy(.regular)
            NSApp.activate()
        }
        .onDisappear {
            navigation.windowVisible = false
            NSApp.setActivationPolicy(.accessory)
        }
        .onChange(of: model.observers.map(\.packageId)) { selectFirst() }
    }

    private func selectFirst() {
        guard navigation.selection == nil, let first = model.installed.first ?? model.uninstalled.first else { return }
        navigation.selection = .observer(first.packageId)
    }
}

struct Sidebar: View {
    let model: RuntimeModel
    @Binding var selection: Navigation.Item?
    @Binding var adding: Bool

    var body: some View {
        List(selection: $selection) {
            if !model.installed.isEmpty {
                Section("Observer") {
                    ForEach(model.installed) { observer in
                        ObserverRow(observer: observer).tag(Navigation.Item.observer(observer.packageId))
                    }
                }
            }
            if !model.uninstalled.isEmpty {
                Section("已卸载") {
                    ForEach(model.uninstalled) { observer in
                        ObserverRow(observer: observer).tag(Navigation.Item.observer(observer.packageId))
                    }
                }
            }
            Section {
                Label("设置", systemImage: "gearshape").tag(Navigation.Item.settings)
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            if let problem = runtimeProblem {
                VStack(alignment: .leading, spacing: 6) {
                    Label(problem, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .font(.callout)
                    if case .failed = model.state {
                        Button("重新启动 Runtime") { model.retryRuntime() }.controlSize(.small)
                    }
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .padding(.horizontal, 10)
                .padding(.top, 6)
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            Button { adding = true } label: {
                Label("添加 Observer…", systemImage: "plus")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.borderless)
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
        }
    }

    private var runtimeProblem: String? {
        switch model.state {
        case .failed, .restarting: ObserverText.runtime(model)
        default: nil
        }
    }
}

struct ObserverRow: View {
    let observer: ObserverStatus

    var body: some View {
        HStack(spacing: 8) {
            ObserverIcon(packageId: observer.packageId, size: 20)
            VStack(alignment: .leading, spacing: 1) {
                Text(observer.name)
                Text(rowDetail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            Circle()
                .fill(ObserverText.color(observer))
                .frame(width: 7, height: 7)
                .help(ObserverText.state(observer))
        }
        .padding(.vertical, 2)
    }

    private var rowDetail: String {
        if !observer.installed, observer.delivery.pending > 0 { return "待发送 \(observer.delivery.pending)" }
        if observer.delivery.rejection != nil { return "发送已暂停" }
        return ObserverText.state(observer)
    }
}

/// A symbol for each known Observer; others share a generic one.
struct ObserverIcon: View {
    let packageId: String
    let size: CGFloat

    var body: some View {
        Image(systemName: packageId == ForegroundToday.packageId ? "macwindow.on.rectangle" : "dot.radiowaves.left.and.right")
            .font(.system(size: size * 0.6, weight: .medium))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(packageId == ForegroundToday.packageId ? Color.blue : Color.indigo,
                        in: RoundedRectangle(cornerRadius: size * 0.24, style: .continuous))
    }
}

/// A rounded box in the style of a grouped form section.
struct Card<Content: View>: View {
    var padding: CGFloat = 12
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) { content }
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quinary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

struct SectionTitle: View {
    let title: String
    let detail: String?

    init(_ title: String, detail: String? = nil) {
        self.title = title
        self.detail = detail
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title).font(.headline)
            if let detail { Text(detail).font(.callout).foregroundStyle(.secondary) }
        }
        .padding(.horizontal, 4)
    }
}

struct PeriodRow: View {
    static let height: CGFloat = 34
    let period: Period

    var body: some View {
        HStack(spacing: 10) {
            AppIcon(identifiers: period.identifiers, size: 18)
            Text(period.name ?? "未知应用")
            if period.isActive {
                Text("进行中")
                    .font(.caption)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(.green.opacity(0.18), in: Capsule())
                    .foregroundStyle(.green)
            }
            Spacer()
            Text("\(Format.time(period.startAt)) – \(Format.time(period.endAt))")
                .monospacedDigit()
                .foregroundStyle(.secondary)
            Text(Format.duration(from: period.startAt, to: period.endAt))
                .monospacedDigit()
                .frame(minWidth: 88, alignment: .trailing)
        }
        .padding(.horizontal, 12)
        .frame(height: Self.height)
    }
}

/// The application's own icon, found by bundle identifier or executable path.
struct AppIcon: View {
    let identifiers: [AppIdentifier]
    let size: CGFloat

    var body: some View {
        if let icon = AppVisuals.icon(for: identifiers) {
            Image(nsImage: icon).resizable().frame(width: size, height: size)
        } else {
            Image(systemName: "app.dashed")
                .resizable()
                .foregroundStyle(.secondary)
                .frame(width: size * 0.8, height: size * 0.8)
                .frame(width: size, height: size)
        }
    }
}

@MainActor
enum AppVisuals {
    private static var icons: [[AppIdentifier]: NSImage?] = [:]
    /// Today's longest applications get these colours in order; the rest share grey.
    private static let palette: [Color] = [.blue, .orange, .purple, .green, .pink, .teal]

    static func colored(_ usage: [Usage]) -> [String] { usage.prefix(palette.count).map(\.id) }

    static func color(for key: String?, among keys: [String]) -> Color {
        key.flatMap { keys.firstIndex(of: $0) }.map { palette[$0] } ?? .gray
    }

    static func icon(for identifiers: [AppIdentifier]) -> NSImage? {
        if let cached = icons[identifiers] { return cached }
        let bundle = identifiers.first { $0.scheme == "macos.bundle-id" }?.value
        let path = bundle.flatMap { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0)?.path }
            ?? identifiers.first { $0.scheme == "macos.executable-path" }?.value
        let icon = path.map { NSWorkspace.shared.icon(forFile: $0) }
        icons[identifiers] = icon
        return icon
    }
}

enum Format {
    static func time(_ date: Date) -> String {
        date.formatted(Date.VerbatimFormatStyle(
            format: "\(hour: .twoDigits(clock: .twentyFourHour, hourCycle: .zeroBased)):\(minute: .twoDigits)",
            timeZone: .current, calendar: .current))
    }

    static func duration(from start: Date, to end: Date) -> String {
        duration(end.timeIntervalSince(start))
    }

    static func duration(_ interval: TimeInterval) -> String {
        let seconds = max(0, Int(interval))
        if seconds == 0 { return "不到 1 秒" }
        let units: Set<Duration.UnitsFormatStyle.Unit> = seconds < 60 ? [.seconds] : [.hours, .minutes]
        return Duration.seconds(seconds).formatted(.units(allowed: units, width: .narrow, maximumUnitCount: 2))
    }
}
