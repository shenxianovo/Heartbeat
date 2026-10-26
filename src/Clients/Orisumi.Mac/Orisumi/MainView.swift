import AppKit
import OrisumiClient
import SwiftUI

/// The main window. The app shows in the Dock only while it is open.
struct MainView: View {
    static let windowId = "main"
    let model: ClientModel
    @Bindable var navigation: Navigation

    var body: some View {
        TabView(selection: $navigation.page) {
            Tab("状态", systemImage: "waveform.path.ecg", value: .status) {
                StatusPage(model: model)
            }
            Tab("设置", systemImage: "gearshape", value: .settings) {
                SettingsPage(model: model)
            }
        }
        .frame(minWidth: 820, minHeight: 680)
        .onAppear {
            NSApp.setActivationPolicy(.regular)
            NSApp.activate()
        }
        .onDisappear { NSApp.setActivationPolicy(.accessory) }
    }
}

/// The status page keeps its header in place; today's applications and the period list scroll inside their own boxes.
struct StatusPage: View {
    let model: ClientModel
    @Environment(\.openURL) private var openURL
    /// The application picked in the legend: the chart highlights it and the period list shows only it.
    @State private var selected: String?
    /// The application under the pointer in the legend.
    @State private var hoveredApp: String?

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            let now = context.date
            let day = Today.interval(containing: now)
            let periods = model.today(now: now)
            let usage = Today.usage(periods, day: day, now: now)
            let shown = selected.map { key in periods.filter { $0.applicationKey == key } } ?? periods
            VStack(alignment: .leading, spacing: 8) {
                Card {
                    header(total: usage.reduce(0) { $0 + $1.duration })
                    if let rejection = model.delivery?.rejection {
                        Label("发送已暂停：\(rejection.rule)，\(rejection.detail)", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.red)
                            .padding(.top, 8)
                    }
                    if let error = model.delivery?.error {
                        Label("发送已暂停：\(error)", systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.red)
                            .padding(.top, 8)
                    }
                }
                .padding(.bottom, 8)

                SectionTitle("今天", detail: model.readError ?? (usage.isEmpty ? nil : "\(usage.count) 个应用"))
                Card {
                    if usage.isEmpty {
                        Text(model.state == .running ? "今天还没有前台时段" : "采集未运行，今天还没有前台时段")
                            .foregroundStyle(.secondary)
                    } else {
                        HStack(alignment: .top, spacing: 20) {
                            TodayChart(periods: periods, usage: usage, day: day, now: now, highlighted: hoveredApp)
                            UsageLegend(usage: usage, selected: $selected, hovered: $hoveredApp)
                                .frame(width: 300, height: TodayChart.height)
                        }
                    }
                }
                .padding(.bottom, 8)

                HStack(spacing: 8) {
                    SectionTitle("时段", detail: shown.isEmpty ? nil : "\(shown.count) 个")
                    if let selected {
                        Button {
                            self.selected = nil
                        } label: {
                            Label("只看 \(usage.first { $0.id == selected }?.name ?? "未知应用")", systemImage: "xmark.circle.fill")
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                        .help("显示全部应用的时段")
                    }
                }
                Card(padding: 0) {
                    ScrollView {
                        LazyVStack(spacing: 0) {
                            ForEach(shown) { period in
                                PeriodRow(period: period, now: now)
                                if period.id != shown.last?.id { Divider().padding(.leading, 40) }
                            }
                        }
                    }
                    .defaultScrollAnchor(.top)
                    .scrollIndicators(.visible)
                    .frame(minHeight: PeriodRow.height * 4, maxHeight: .infinity)
                }
                Text("其他日期的记录在 Web 控制台中查看。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 4)
            }
            .padding(20)
        }
        .toolbar {
            ToolbarItem {
                Button("打开 Web 控制台", systemImage: "safari") { openURL(Preferences.webConsole) }
                    .help("打开 Web 控制台")
            }
        }
    }

    private func header(total: TimeInterval) -> some View {
        HStack(spacing: 14) {
            Image("Mascot")
                .resizable()
                .frame(width: 56, height: 56)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Image(systemName: "circle.fill").foregroundStyle(stateColor).imageScale(.small)
                    Text(StateText.title(model.state)).font(.headline)
                    if total > 0 {
                        Text("· 今天 \(Format.duration(total))").font(.headline).foregroundStyle(.secondary)
                    }
                }
                Text(deliveryText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Spacer()
            switch model.state {
            case .running: Button("暂停采集") { Task { await model.pause() } }
            case .paused: Button("继续采集") { model.start() }.buttonStyle(.borderedProminent)
            case .failed: Button("重新启动") { model.start() }.buttonStyle(.borderedProminent)
            }
        }
    }

    private var deliveryText: String {
        guard let delivery = model.delivery else { return Preferences.backend.absoluteString }
        return "待发送 \(delivery.pending) · 已保存 \(delivery.saved) · \(Preferences.backend.absoluteString)"
    }

    private var stateColor: Color {
        switch model.state {
        case .running: .green
        case .paused: .secondary
        case .failed: .red
        }
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
    let now: Date

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
            Text(period.isActive ? "\(Format.time(period.startAt)) 起" : "\(Format.time(period.startAt)) – \(Format.time(period.endAt))")
                .monospacedDigit()
                .foregroundStyle(.secondary)
            Text(Format.duration(from: period.startAt, to: period.isActive ? max(now, period.endAt) : period.endAt))
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
