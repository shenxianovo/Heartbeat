import AppKit
import OrisumiClient
import SwiftUI

/// One Observer's data in the main window, under a banner when something needs attention.
struct ObserverPage: View {
    let model: RuntimeModel
    let observer: ObserverStatus
    /// Opens the Observer on this Mac's page.
    let manage: () -> Void
    @Environment(\.openURL) private var openURL

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                StatusBanners(model: model, observer: observer, manage: manage)
                if observer.packageId == ForegroundToday.packageId {
                    ForegroundToday(model: model, instanceId: observer.instanceId)
                } else {
                    RecentObservations(model: model, instanceId: observer.instanceId)
                }
            }
            .padding(24)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .navigationTitle(ObserverText.viewName(observer))
        .navigationSubtitle(subtitle)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Web 控制台", systemImage: "arrow.up.forward.app") { openURL(Preferences.webConsole) }
                    .labelStyle(.titleAndIcon)
                    .help("在 Web 控制台中查看其他日期和追溯")
            }
        }
    }

    private var subtitle: String {
        observer.packageId == ForegroundToday.packageId
            ? Date.now.formatted(.dateTime.month().day().weekday(.wide))
            : "最近 7 天"
    }
}

/// What keeps this view from being complete or current: Runtime first, then the Observer's own state.
struct StatusBanners: View {
    let model: RuntimeModel
    let observer: ObserverStatus
    let manage: () -> Void

    var body: some View {
        let busy = model.busy.contains(observer.packageId)
        switch model.state {
        case .failed(let message):
            Banner(tone: .critical, title: "Runtime 已停止，Observer 都不在记录", message: message) {
                Button("重新启动 Runtime") { model.retryRuntime() }
            }
        case .restarting:
            Banner(tone: .warning, title: "Runtime 意外退出，正在重新启动", message: nil) { EmptyView() }
        case .running:
            if observer.state == .failed {
                Banner(tone: .critical, title: "\(observer.name) 停止记录了", message: observer.error) {
                    Button("重试") { Task { await model.retry(observer.packageId) } }.disabled(busy)
                    Button("详情…", action: manage)
                }
            } else if observer.state == .waiting {
                Banner(tone: .warning, title: "\(observer.name) 意外退出，正在等待重启", message: nil) { EmptyView() }
            } else if !observer.enabled {
                Banner(tone: .info, title: "\(observer.name) 已停用，现在不在记录", message: "停用期间在记录中是空白。") {
                    Button("启用") { Task { await model.setEnabled(observer.packageId, true) } }.disabled(busy)
                }
            }
            if let rejection = observer.delivery.rejection {
                Banner(tone: .warning, title: "发送已暂停", message: "\(rejection.rule)，\(rejection.detail)") {
                    Button("详情…", action: manage)
                }
            } else if let error = observer.delivery.error {
                Banner(tone: .warning, title: "发送出错，新的记录暂时留在本机", message: error) { EmptyView() }
            }
            if let diagnostic = observer.diagnostic {
                Banner(tone: .info, title: "采集提示", message: diagnostic) { EmptyView() }
            }
        case .starting, .stopped:
            EmptyView()
        }
    }
}

struct Banner<Actions: View>: View {
    enum Tone {
        case critical, warning, info

        var color: Color {
            switch self {
            case .critical: .red
            case .warning: .orange
            case .info: .secondary
            }
        }

        var symbol: String {
            switch self {
            case .critical: "exclamationmark.octagon.fill"
            case .warning: "exclamationmark.triangle.fill"
            case .info: "info.circle.fill"
            }
        }
    }

    let tone: Tone
    let title: String
    let message: String?
    @ViewBuilder let actions: Actions

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: tone.symbol)
                .font(.title3)
                .foregroundStyle(tone.color)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).fontWeight(.medium)
                if let message {
                    SelectableBannerMessage(message: message)
                }
            }
            Spacer(minLength: 12)
            HStack(spacing: 8) { actions }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(tone.color.opacity(0.08), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(tone.color.opacity(0.22), lineWidth: 0.5))
    }
}

/// Use AppKit's label for selectable text inside the scrolling banner. SwiftUI's
/// selectable Text can draw vertically flipped in this scroll view.
private struct SelectableBannerMessage: NSViewRepresentable {
    let message: String

    func makeNSView(context: Context) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: message)
        label.isSelectable = true
        label.font = .preferredFont(forTextStyle: .callout)
        label.textColor = .secondaryLabelColor
        label.maximumNumberOfLines = 3
        label.lineBreakMode = .byWordWrapping
        label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return label
    }

    func updateNSView(_ label: NSTextField, context: Context) {
        if label.stringValue != message { label.stringValue = message }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSTextField, context: Context) -> CGSize? {
        guard let width = proposal.width, width.isFinite else { return nil }
        let size = nsView.cell!.cellSize(forBounds: NSRect(x: 0, y: 0, width: width, height: .greatestFiniteMagnitude))
        return CGSize(width: width, height: ceil(size.height))
    }
}

/// The latest Observations of an Observer that has no dedicated view.
struct RecentObservations: View {
    let model: RuntimeModel
    let instanceId: UUID
    @State private var reader = RecentReader()
    @State private var snapshot: Snapshot<RecentObservation>?
    @State private var readError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("观测", detail: detail)
            Card(padding: 0) {
                if let items = snapshot?.items, !items.isEmpty {
                    LazyVStack(spacing: 0) {
                        ForEach(items) { item in
                            RecentRow(item: item)
                            if item.id != items.last?.id { Divider().padding(.leading, 12) }
                        }
                    }
                } else {
                    Text(snapshot == nil ? "正在读取…" : "最近 7 天没有观测")
                        .foregroundStyle(.secondary)
                        .padding(12)
                }
            }
        }
        .task(id: instanceId) {
            snapshot = nil
            while !Task.isCancelled {
                do {
                    snapshot = try await reader.recent(instanceId: instanceId, now: .now, from: model)
                    readError = nil
                } catch {
                    readError = error.localizedDescription
                }
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    private var detail: String? {
        if let readError { return "无法读取：\(readError)" }
        if let snapshot, !snapshot.complete { return "后端不可用，只显示本机的数据，可能不完整" }
        return snapshot.map { "\($0.items.count) 份" }
    }
}

struct RecentRow: View {
    let item: RecentObservation

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(time)
                .monospacedDigit()
                .foregroundStyle(.secondary)
                .frame(width: 190, alignment: .leading)
            StateBadge(state: item.state)
            Text(item.content?.compact ?? "内容不可读")
                .font(.callout.monospaced())
                .lineLimit(1)
                .truncationMode(.tail)
                .foregroundStyle(item.content == nil ? .secondary : .primary)
                .help(item.content?.compact ?? "")
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }

    private var time: String {
        guard let start = item.startAt else { return "时间未知" }
        let day = start.formatted(.dateTime.month().day())
        guard let end = item.endAt, end != start else { return "\(day) \(Format.time(start))" }
        return "\(day) \(Format.time(start)) – \(Format.time(end))"
    }
}

struct StateBadge: View {
    let state: LocalState

    var body: some View {
        Text(text)
            .font(.caption)
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background(color.opacity(0.16), in: Capsule())
            .foregroundStyle(color)
    }

    private var text: String {
        switch state {
        case .draft: "进行中"
        case .pending: "待发送"
        case .saved: "已保存"
        case .rejected: "被拒绝"
        }
    }

    private var color: Color {
        switch state {
        case .draft: .green
        case .pending: .orange
        case .saved: .secondary
        case .rejected: .red
        }
    }
}
