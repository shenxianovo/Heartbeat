import AppKit
import OrisumiClient
import SwiftUI

/// The main window: each Observer's data, and the Observers on this Mac and the other devices, to use and manage.
/// How Orisumi behaves by default is in Settings.
struct MainView: View {
    static let windowId = "main"
    let model: RuntimeModel
    let management: ManagementModel
    @Bindable var navigation: Navigation
    @State private var pairing = false

    var body: some View {
        NavigationSplitView {
            Sidebar(model: model, management: management, selection: $navigation.selection, pair: { pairing = true })
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 300)
        } detail: {
            switch navigation.selection {
            case .view(let packageId):
                if let observer = model.observer(packageId), observer.installed {
                    ObserverPage(model: model, observer: observer) {
                        navigation.managed = packageId
                        navigation.selection = .thisMac
                    }
                    .id(packageId)
                } else {
                    ContentUnavailableView("这个 Observer 已卸载", systemImage: "puzzlepiece.extension")
                }
            case .host(let hostId):
                if let host = management.hosts.first(where: { $0.id == hostId }) {
                    RemoteHostPage(host: host, management: management, navigation: navigation).id(hostId)
                } else {
                    ContentUnavailableView("读不到这台机器", systemImage: "network.slash",
                                           description: management.error.map { Text($0) })
                }
            case .thisMac, nil:
                ThisMacPage(model: model, management: management, navigation: navigation)
            }
        }
        .frame(minWidth: 900, minHeight: 600)
        .sheet(isPresented: $pairing) { PairingSheet(host: nil, management: management) }
        .onAppear {
            navigation.windowVisible = true
            if navigation.selection == nil {
                navigation.selection = model.installed.first.map { .view($0.packageId) } ?? .thisMac
            }
            Task {
                await model.refresh()
                await model.refreshCatalog()
            }
            NSApp.activate()
        }
        .onDisappear { navigation.windowVisible = false }
        .task {
            while !Task.isCancelled {
                await management.refresh()
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }
}

struct Sidebar: View {
    let model: RuntimeModel
    let management: ManagementModel
    @Binding var selection: Navigation.Destination?
    let pair: () -> Void
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        List(selection: $selection) {
            if !model.installed.isEmpty {
                Section("记录") {
                    ForEach(model.installed) { observer in
                        SidebarRow(observer: observer).tag(Navigation.Destination.view(observer.packageId))
                    }
                }
            }
            Section("设备") {
                HStack {
                    Label("这台 Mac", systemImage: "laptopcomputer")
                    Spacer(minLength: 4)
                    if ObserverText.hasFailure(model) {
                        Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.red)
                    }
                }
                .tag(Navigation.Destination.thisMac)
                ForEach(remoteHosts) { host in
                    HStack {
                        Label(host.name, systemImage: host.kind == "server" ? "server.rack" : "laptopcomputer")
                            .foregroundStyle(host.revoked ? .secondary : .primary)
                        Spacer(minLength: 4)
                        if host.revoked {
                            Text("已移除").font(.caption).foregroundStyle(.secondary)
                        } else if !host.connected {
                            Text("离线").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .tag(Navigation.Destination.host(host.id))
                }
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            HStack {
                Button(action: pair) {
                    Label("接入设备", systemImage: "plus")
                }
                .help("签发配对码，接入一台设备或服务器")
                Spacer()
                Button("设置", systemImage: "gearshape") { openSettings() }
                    .labelStyle(.iconOnly)
                    .help("外观、登录时启动和连接（⌘,）")
            }
            .buttonStyle(.borderless)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
    }

    /// Hosts other than this Mac's own Runtime; removed ones go last.
    private var remoteHosts: [ManagedHost] {
        management.hosts.filter { $0.id != model.host?.hostId }.sorted { !$0.revoked && $1.revoked }
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
