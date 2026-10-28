import AppKit
import OrisumiClient
import SwiftUI

/// One Observer: whether it is enabled, how its process and sending are doing, and its data.
struct ObserverDetail: View {
    let model: RuntimeModel
    let observer: ObserverStatus
    @Environment(\.openURL) private var openURL

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ObserverHeader(model: model, observer: observer)
                .padding(.bottom, 8)
            if observer.packageId == ForegroundToday.packageId {
                ForegroundToday(model: model, instanceId: observer.instanceId)
            } else {
                RecentObservations(model: model, instanceId: observer.instanceId)
            }
        }
        .padding(20)
        .navigationTitle(observer.name)
        .toolbar {
            ToolbarItem {
                Button("打开 Web 控制台", systemImage: "safari") { openURL(Preferences.webConsole) }
                    .help("打开 Web 控制台")
            }
        }
    }
}

struct ObserverHeader: View {
    let model: RuntimeModel
    let observer: ObserverStatus
    @State private var confirmingUninstall = false

    var body: some View {
        Card {
            HStack(spacing: 14) {
                ObserverIcon(packageId: observer.packageId, size: 48)
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text(observer.name).font(.title3.weight(.semibold))
                        if let version = observer.version {
                            Text(version).font(.callout).foregroundStyle(.secondary).monospacedDigit()
                        }
                    }
                    HStack(spacing: 6) {
                        Image(systemName: "circle.fill").foregroundStyle(ObserverText.color(observer)).imageScale(.small)
                        Text(ObserverText.state(observer))
                        Text("·").foregroundStyle(.tertiary)
                        Text(ObserverText.delivery(observer.delivery)).foregroundStyle(.secondary).monospacedDigit()
                    }
                    .font(.callout)
                }
                Spacer()
                actions
            }
            ForEach(problems, id: \.self) { problem in
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .padding(.top, 8)
            }
            if let diagnostic = observer.diagnostic {
                Label("采集提示：\(diagnostic)", systemImage: "info.circle")
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                    .textSelection(.enabled)
                    .padding(.top, 8)
            }
            if observer.delivery.rejection != nil, !(observer.installed && observer.enabled) {
                Text("这个 Observer 再次运行后，由它处理被拒绝的版本。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
            }
        }
        .confirmationDialog("卸载 \(observer.name)？", isPresented: $confirmingUninstall) {
            Button("卸载", role: .destructive) { Task { await model.uninstall(observer.packageId) } }
        } message: {
            Text("程序会被移除。程序实例的身份、设置和本地数据保留，待发送的数据继续发送；重新安装后沿用原来的身份。")
        }
    }

    @ViewBuilder private var actions: some View {
        let busy = model.busy.contains(observer.packageId)
        HStack(spacing: 10) {
            if busy { ProgressView().controlSize(.small) }
            if observer.installed {
                if observer.state == .failed {
                    Button("重试") { Task { await model.retry(observer.packageId) } }
                        .disabled(busy)
                }
                if let update = model.update(for: observer.packageId) {
                    Button("更新到 \(update.version)") { Task { await model.updatePackage(observer.packageId) } }
                        .disabled(busy)
                        .help("下载并校验新版本后替换，身份、设置和启用状态不变")
                }
                Toggle("启用", isOn: Binding(get: { observer.enabled },
                                            set: { enabled in Task { await model.setEnabled(observer.packageId, enabled) } }))
                    .toggleStyle(.switch)
                    .disabled(busy)
                Menu {
                    Button("卸载…", role: .destructive) { confirmingUninstall = true }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .disabled(busy)
            } else {
                Button("重新安装") { Task { await model.install(observer.packageId) } }
                    .buttonStyle(.borderedProminent)
                    .disabled(busy)
            }
        }
    }

    private var problems: [String] {
        var problems: [String] = []
        if let error = observer.error { problems.append(error) }
        if let rejection = observer.delivery.rejection {
            problems.append("发送已暂停：\(rejection.rule)，\(rejection.detail)")
        }
        if let error = observer.delivery.error { problems.append("发送出错：\(error)") }
        if let error = model.operationError, model.busy.isEmpty { problems.append(error) }
        return problems
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
        SectionTitle("最近的观测", detail: detail)
        Card(padding: 0) {
            if let items = snapshot?.items, !items.isEmpty {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(items) { item in
                            RecentRow(item: item)
                            if item.id != items.last?.id { Divider().padding(.leading, 12) }
                        }
                    }
                }
                .frame(maxHeight: .infinity)
            } else {
                Text(snapshot == nil ? "正在读取…" : "最近 7 天没有观测")
                    .foregroundStyle(.secondary)
                    .padding(12)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        Text("这个 Observer 没有专用视图。更早的数据和追溯在 Web 控制台中查看。")
            .font(.callout)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 4)
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
        return snapshot.map { "最近 7 天的 \($0.items.count) 份" }
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

/// Packages from the official catalog and the bundled ones that this Mac can install.
struct AddObserverSheet: View {
    let model: RuntimeModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("添加 Observer").font(.title2.weight(.semibold))
            Text("安装后直接开始采集，之后可以随时停用。")
                .foregroundStyle(.secondary)
            List(model.catalog) { package in
                CatalogRow(model: model, package: package)
            }
            .listStyle(.bordered)
            .overlay {
                if model.catalog.isEmpty {
                    Text(model.catalogError ?? "没有可安装的 Observer").foregroundStyle(.secondary)
                }
            }
            HStack {
                if let error = model.catalogError, !model.catalog.isEmpty {
                    Label("无法读取官方目录：\(error)", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(.secondary)
                        .font(.callout)
                }
                if let error = model.operationError, model.busy.isEmpty {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .font(.callout)
                }
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 560, height: 440)
        .task { await model.refreshCatalog() }
    }
}

struct CatalogRow: View {
    let model: RuntimeModel
    let package: CatalogPackage

    var body: some View {
        HStack(spacing: 12) {
            ObserverIcon(packageId: package.id, size: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(package.name).font(.headline)
                if let description = package.description { Text(description).foregroundStyle(.secondary) }
                Text("\(package.version) · \(package.target)")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .monospacedDigit()
            }
            Spacer()
            if model.busy.contains(package.id) {
                ProgressView().controlSize(.small)
            } else if !package.compatible {
                Text("不适用于这台 Mac").foregroundStyle(.secondary)
            } else if package.hasUpdate {
                Button("更新") { Task { await model.updatePackage(package.id) } }
            } else if package.installedVersion != nil {
                Label("已安装", systemImage: "checkmark").foregroundStyle(.secondary)
            } else {
                Button("安装") { Task { await model.install(package.id) } }
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(.vertical, 4)
    }
}
