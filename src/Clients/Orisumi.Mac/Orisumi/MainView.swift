import AppKit
import OrisumiClient
import SwiftUI

/// The main window: one view of data for each installed Observer. Managing Observers is in Settings.
struct MainView: View {
    static let windowId = "main"
    let model: RuntimeModel
    @Bindable var navigation: Navigation
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        NavigationSplitView {
            Sidebar(model: model, selection: $navigation.selection) { settings(.general) }
                .navigationSplitViewColumnWidth(min: 180, ideal: 200, max: 280)
        } detail: {
            if let packageId = navigation.selection, let observer = model.observer(packageId), observer.installed {
                ObserverPage(model: model, observer: observer, openSettings: settings)
                    .id(packageId)
            } else if model.installed.isEmpty {
                ContentUnavailableView {
                    Label("还没有在记录的数据", systemImage: "puzzlepiece.extension")
                } description: {
                    Text("添加一个 Observer，它会直接开始记录。")
                } actions: {
                    Button("添加 Observer…") { settings(.observers, adding: true) }
                }
            } else {
                ContentUnavailableView("选择一个视图", systemImage: "sidebar.left")
            }
        }
        .frame(minWidth: 860, minHeight: 600)
        .onAppear {
            navigation.windowVisible = true
            selectFirst()
            Task {
                await model.refresh()
                await model.refreshCatalog()
            }
            NSApp.activate()
        }
        .onDisappear { navigation.windowVisible = false }
        .onChange(of: model.installed.map(\.packageId)) { selectFirst() }
    }

    private var settings: SettingsOpener { SettingsOpener(navigation: navigation, openSettings: openSettings) }

    /// Keeps a view selected while there is one.
    private func selectFirst() {
        if let selection = navigation.selection, model.installed.contains(where: { $0.packageId == selection }) { return }
        navigation.selection = model.installed.first?.packageId
    }
}

struct Sidebar: View {
    let model: RuntimeModel
    @Binding var selection: String?
    let openSettings: () -> Void

    var body: some View {
        List(selection: $selection) {
            ForEach(model.installed) { observer in
                SidebarRow(observer: observer).tag(observer.packageId)
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            Button(action: openSettings) {
                Label("设置", systemImage: "gearshape")
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .help("Observer 的管理、外观和连接（⌘,）")
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
    }
}

struct SidebarRow: View {
    let observer: ObserverStatus

    var body: some View {
        HStack(spacing: 8) {
            ObserverIcon(packageId: observer.packageId, size: 20)
            Text(ObserverText.viewName(observer))
            Spacer(minLength: 4)
            if !ObserverText.problems(observer).isEmpty || observer.state == .failed {
                Image(systemName: "exclamationmark.circle.fill")
                    .foregroundStyle(.red)
                    .help(ObserverText.state(observer))
            } else if !observer.enabled {
                Text("已停用").font(.caption).foregroundStyle(.secondary)
            } else if observer.state != .running {
                Image(systemName: "clock")
                    .foregroundStyle(.orange)
                    .help(ObserverText.state(observer))
            }
        }
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

/// One period in time order: when, which application, how long.
struct PeriodRow: View {
    static let height: CGFloat = 30
    let period: Period

    var body: some View {
        HStack(spacing: 10) {
            Text(Format.span(from: period.startAt, to: period.endAt))
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .frame(width: 96, alignment: .leading)
            AppIcon(identifiers: period.identifiers, size: 16)
            Text(period.name ?? "未知应用").lineLimit(1)
            if period.isActive {
                Text("进行中")
                    .font(.caption)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(.green.opacity(0.18), in: Capsule())
                    .foregroundStyle(.green)
            }
            Spacer()
            Text(Format.duration(from: period.startAt, to: period.endAt))
                .monospacedDigit()
                .foregroundStyle(period.endAt.timeIntervalSince(period.startAt) < 60 ? .secondary : .primary)
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

    /// "09:12 – 09:40", or just the start when both fall in the same minute.
    static func span(from start: Date, to end: Date) -> String {
        let first = time(start), last = time(end)
        return first == last ? first : "\(first) – \(last)"
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
