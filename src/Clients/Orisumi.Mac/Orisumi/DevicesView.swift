import AppKit
import OrisumiClient
import SwiftUI

// MARK: - This Mac

/// The Observers on this Mac, managed through Runtime so that changes take effect offline.
struct ThisMacPage: View {
    let model: RuntimeModel
    let management: ManagementModel
    @Bindable var navigation: Navigation
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        List(selection: $navigation.managed) {
            if !model.installed.isEmpty {
                Section("已安装") {
                    ForEach(model.installed) { LocalRow(model: model, observer: $0).tag($0.packageId) }
                }
            }
            if !model.uninstalled.isEmpty {
                Section {
                    ForEach(model.uninstalled) { LocalRow(model: model, observer: $0).tag($0.packageId) }
                } header: {
                    Text("已卸载")
                } footer: {
                    Text("还没发送的数据继续发送。重新安装后沿用原来的身份和设置。")
                }
            }
        }
        .overlay {
            if model.observers.isEmpty {
                ContentUnavailableView {
                    Label("还没有 Observer", systemImage: "puzzlepiece.extension")
                } description: {
                    Text("添加一个 Observer，它会直接开始记录。")
                } actions: {
                    Button("添加 Observer…") { navigation.addingObserver = true }
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { joinBanner }
        .navigationTitle("这台 Mac")
        .navigationSubtitle(subtitle)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("添加 Observer", systemImage: "plus") { navigation.addingObserver = true }
                    .help("从目录安装一个 Observer")
            }
        }
        .inspector(isPresented: Binding(get: { navigation.managed != nil },
                                        set: { if !$0 { navigation.managed = nil } })) {
            if let packageId = navigation.managed, let observer = model.observer(packageId) {
                LocalInspector(model: model, management: management, observer: observer) {
                    navigation.selection = .view(packageId)
                }
                .inspectorColumnWidth(min: 300, ideal: 340, max: 440)
            }
        }
        .sheet(isPresented: $navigation.addingObserver) {
            AddObserverSheet(model: model) { navigation.selection = .view($0) }
        }
    }

    private var subtitle: String {
        let running = model.installed.filter { $0.state == .running }.count
        let state = switch model.host?.connection {
        case "connected": "在线"
        case "offline": "离线"
        case "revoked": "已被移除"
        default: "未接入"
        }
        return "\(state) · \(running) 个在采集"
    }

    @ViewBuilder private var joinBanner: some View {
        switch model.host?.connection {
        case "revoked":
            Banner(tone: .warning, title: "这台 Mac 已被移除", message: "照常在本机记录。重新接入后，继续同步和发送。") {
                Button("重新接入…") { SettingsOpener(navigation: navigation, openSettings: openSettings)(.connection) }
            }
            .padding([.horizontal, .top], 16)
        case nil, "unpaired":
            Banner(tone: .info, title: "这台 Mac 还没有接入", message: "现在只在本机记录。接入后，其他设备也能看到和管理它。") {
                Button("接入…") { SettingsOpener(navigation: navigation, openSettings: openSettings)(.connection) }
            }
            .padding([.horizontal, .top], 16)
        default:
            EmptyView()
        }
    }
}

struct LocalRow: View {
    let model: RuntimeModel
    let observer: ObserverStatus

    var body: some View {
        HStack(spacing: 10) {
            ObserverIcon(packageId: observer.packageId, size: 28)
                .opacity(observer.installed && !observer.removed ? 1 : 0.5)
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(observer.name)
                    if let version = observer.version {
                        Text(version).font(.caption).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                HStack(spacing: 5) {
                    Circle().fill(ObserverText.color(observer)).frame(width: 6, height: 6)
                    Text(observer.removed ? "已删除" : ObserverText.state(observer))
                    Text("·").foregroundStyle(.tertiary)
                    Text(ObserverText.deliverySummary(observer.delivery))
                    if observer.sync.pendingChanges > 0 {
                        Text("·").foregroundStyle(.tertiary)
                        Text("等待同步")
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            }
            Spacer(minLength: 8)
            if model.busy.contains(observer.packageId) { ProgressView().controlSize(.small) }
            if observer.installed && !observer.removed {
                Toggle("启用", isOn: Binding(get: { observer.enabled },
                                            set: { enabled in Task { await model.setEnabled(observer.packageId, enabled) } }))
                    .toggleStyle(.switch)
                    .labelsHidden()
                    .controlSize(.small)
                    .disabled(model.busy.contains(observer.packageId))
            }
        }
        .padding(.vertical, 4)
    }
}

/// Everything about one Observer on this Mac: state, settings, maintenance and removal.
struct LocalInspector: View {
    let model: RuntimeModel
    let management: ManagementModel
    let observer: ObserverStatus
    let show: () -> Void
    @State private var uninstalling = false
    @State private var removing = false

    var body: some View {
        let busy = model.busy.contains(observer.packageId)
        Form {
            Section {
                InspectorTitle(packageId: observer.packageId, name: observer.name, detail: observer.version.map { "版本 \($0)" })
            }
            Section("状态") {
                LabeledContent("运行") {
                    HStack(spacing: 6) {
                        Circle().fill(ObserverText.color(observer)).frame(width: 7, height: 7)
                        Text(observer.removed ? "已删除" : ObserverText.state(observer))
                    }
                }
                LabeledContent("发送", value: ObserverText.delivery(observer.delivery))
                LabeledContent("同步", value: synchronization)
                ForEach(ObserverText.problems(observer), id: \.self) { problem in
                    Text(problem).foregroundStyle(.red).textSelection(.enabled)
                }
                if let diagnostic = observer.diagnostic {
                    Text(diagnostic).foregroundStyle(.secondary).textSelection(.enabled)
                }
            }
            if observer.installed && !observer.removed {
                SettingsSection(settings: observer.settings, disabled: busy) { value in
                    Task { await model.setSettings(observer.packageId, settings: value) }
                }
            }
            Section("维护") {
                if observer.removed {
                    Button("新建实例…") { Task { await model.install(observer.packageId, newInstance: true) } }
                        .disabled(busy)
                } else if !observer.installed {
                    Button("重新安装") { Task { await model.install(observer.packageId) } }.disabled(busy)
                } else {
                    if let update = model.update(for: observer.packageId) {
                        Button("更新到 \(update.version)") { Task { await model.updatePackage(observer.packageId) } }
                            .disabled(busy)
                    }
                    if observer.state == .failed {
                        Button("重试") { Task { await model.retry(observer.packageId) } }.disabled(busy)
                    }
                    Button("在窗口中查看") { show() }
                    Button("卸载…") { uninstalling = true }.disabled(busy)
                }
                if let error = model.operationError, !busy {
                    Text(error).foregroundStyle(.red).textSelection(.enabled)
                }
            }
            if !observer.removed {
                Section {
                    Button("删除实例…", role: .destructive) { removing = true }
                        .disabled(model.host?.connection != "connected" || management.busy)
                } footer: {
                    Text(model.host?.connection == "connected"
                         ? "永久结束这个程序实例的身份。卸载只移除程序，身份保留。"
                         : "接入并在线时才能删除实例。")
                }
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("卸载 \(observer.name)？", isPresented: $uninstalling) {
            Button("卸载", role: .destructive) { Task { await model.uninstall(observer.packageId) } }
        } message: {
            Text("程序会被移除，身份、设置和还没发送的数据都保留。")
        }
        .confirmationDialog("永久删除 \(observer.name)？", isPresented: $removing) {
            Button("删除", role: .destructive) { Task { await management.remove(observer.instanceId) } }
        } message: {
            Text("它以前记下的数据保留。以后再安装，会是一个新的程序实例。")
        }
    }

    private var synchronization: String {
        if model.host?.hostId == nil { return "只在本机" }
        if let error = observer.sync.error { return error }
        return observer.sync.pendingChanges > 0 ? "\(observer.sync.pendingChanges) 项修改等待同步" : "已同步"
    }
}

// MARK: - Other hosts

/// Another device or server's Observers, managed through the backend.
struct RemoteHostPage: View {
    let host: ManagedHost
    let management: ManagementModel
    @Bindable var navigation: Navigation
    @State private var selected: UUID?
    @State private var installing = false
    @State private var pairing = false
    @State private var renaming = false
    @State private var revoking = false
    @State private var name = ""

    var body: some View {
        let instances = management.instances.filter { $0.hostId == host.id }
        let current = instances.filter { !$0.removed }
        let removed = instances.filter(\.removed)
        List(selection: $selected) {
            if !current.isEmpty {
                Section("Observer") {
                    ForEach(current) { RemoteRow(host: host, instance: $0, management: management).tag($0.id) }
                }
            }
            if !removed.isEmpty {
                Section("已删除") {
                    ForEach(removed) { instance in
                        HStack {
                            Text(instance.name).foregroundStyle(.secondary)
                            Spacer()
                            Text(instance.state == "stopped" ? "已停止" : "等待设备停止")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .overlay {
            if current.isEmpty && removed.isEmpty {
                ContentUnavailableView("还没有 Observer", systemImage: "puzzlepiece.extension",
                                       description: Text(host.connected ? "从目录安装一个 Observer。" : "它连接后才能安装。"))
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) { banner }
        .navigationTitle(host.name)
        .navigationSubtitle(host.kind == "server" ? "服务器 · \(HostText.connection(host))" : "设备 · \(HostText.connection(host))")
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                if !host.revoked {
                    Button("安装", systemImage: "plus") { installing = true }
                        .disabled(!host.connected)
                        .help(host.connected ? "从目录安装一个 Observer" : "它连接后才能安装")
                }
                Menu("更多", systemImage: "ellipsis.circle") {
                    Button("改名…") { name = host.name; renaming = true }
                    if host.revoked {
                        Button("重新接入…") { pairing = true }
                    } else {
                        Button("移除…", role: .destructive) { revoking = true }
                    }
                }
            }
        }
        .inspector(isPresented: Binding(get: { selected != nil }, set: { if !$0 { selected = nil } })) {
            if let instance = management.instances.first(where: { $0.id == selected }) {
                RemoteInspector(host: host, instance: instance, management: management)
                    .inspectorColumnWidth(min: 300, ideal: 340, max: 440)
            }
        }
        .sheet(isPresented: $installing) { RemotePackageSheet(host: host, management: management) }
        .sheet(isPresented: $pairing) { PairingSheet(host: host, management: management) }
        .alert("重命名", isPresented: $renaming) {
            TextField("名称", text: $name)
            Button("保存") { Task { await management.rename(host.id, name: name) } }
            Button("取消", role: .cancel) {}
        }
        .confirmationDialog("移除 \(host.name)？", isPresented: $revoking) {
            Button("移除", role: .destructive) { Task { await management.revoke(host.id) } }
        } message: {
            Text("它不能再同步和发送，已保存的数据都留着。之后可以重新接入，沿用原来的身份。")
        }
    }

    @ViewBuilder private var banner: some View {
        if host.revoked {
            Banner(tone: .info, title: "已移除", message: "它不再同步。重新接入后，沿用原来的身份。") {
                Button("重新接入…") { pairing = true }
            }
            .padding([.horizontal, .top], 16)
        } else if !host.connected {
            Banner(tone: .info, title: HostText.connection(host), message: "启用和设置会在它连接后执行，运行状态现在未知。") {
                EmptyView()
            }
            .padding([.horizontal, .top], 16)
        }
    }
}

struct RemoteRow: View {
    let host: ManagedHost
    let instance: ManagedInstance
    let management: ManagementModel

    var body: some View {
        HStack(spacing: 10) {
            ObserverIcon(packageId: instance.instanceKey, size: 28)
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(instance.name)
                    if let version = instance.version {
                        Text(version).font(.caption).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
                HStack(spacing: 5) {
                    Circle().fill(HostText.color(host, instance)).frame(width: 6, height: 6)
                    Text(HostText.state(host, instance))
                    if instance.pending > 0 {
                        Text("·").foregroundStyle(.tertiary)
                        Text("待发送 \(instance.pending)")
                    }
                    if !instance.synchronized {
                        Text("·").foregroundStyle(.tertiary)
                        Text("等待同步")
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
            }
            Spacer(minLength: 8)
            Toggle("启用", isOn: Binding(get: { instance.enabled },
                                        set: { enabled in Task { await management.configure(instance.id, enabled: enabled) } }))
                .toggleStyle(.switch)
                .labelsHidden()
                .controlSize(.small)
                .disabled(host.revoked || management.busy)
        }
        .padding(.vertical, 4)
    }
}

struct RemoteInspector: View {
    let host: ManagedHost
    let instance: ManagedInstance
    let management: ManagementModel
    @State private var removing = false

    var body: some View {
        let writable = !host.revoked && !instance.removed
        Form {
            Section {
                InspectorTitle(packageId: instance.instanceKey, name: instance.name, detail: instance.version.map { "版本 \($0)" })
            }
            Section("状态") {
                LabeledContent("配置", value: instance.enabled ? "启用" : "停用")
                LabeledContent("同步", value: instance.synchronized ? "设备已执行最新的配置" : "等待设备同步")
                LabeledContent("运行") {
                    HStack(spacing: 6) {
                        Circle().fill(HostText.color(host, instance)).frame(width: 7, height: 7)
                        Text(HostText.state(host, instance))
                    }
                }
                if !host.connected, let reported = instance.reportedAt {
                    LabeledContent("最近报告", value: reported.formatted(.relative(presentation: .named)))
                }
                LabeledContent("发送", value: instance.pending > 0 ? "\(instance.pending) 个待发送" : "都已发送")
                if let problem = instance.problem {
                    Text(problem).foregroundStyle(.red).textSelection(.enabled)
                }
            }
            if writable {
                SettingsSection(settings: instance.settings, disabled: management.busy) { value in
                    Task { await management.configure(instance.id, settings: value) }
                }
                Section {
                    HStack {
                        Button("更新") { run("update") }
                        Button("重试") { run("retry") }
                        Button("卸载") { run("uninstall") }
                    }
                    .disabled(!host.connected || management.busy)
                } header: {
                    Text("维护")
                } footer: {
                    Text(maintenanceNote)
                }
                Section {
                    Button("删除实例…", role: .destructive) { removing = true }.disabled(management.busy)
                } footer: {
                    Text("永久结束这个程序实例的身份。卸载只移除程序，身份保留。")
                }
            }
            if let error = management.error {
                Text(error).foregroundStyle(.red).textSelection(.enabled)
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("永久删除 \(instance.name)？", isPresented: $removing) {
            Button("删除", role: .destructive) { Task { await management.remove(instance.id) } }
        } message: {
            Text("它以前记下的数据保留。以后再安装，会是一个新的程序实例。")
        }
    }

    private func run(_ action: String) {
        Task { await management.execute(host: host.id, action: action, instance: instance.id) }
    }

    private var maintenanceNote: String {
        if let operation = management.operation, operation.entityId == instance.id {
            return HostText.operation(operation)
        }
        return host.connected ? "更新到目录中的最新版本，或重新启动失败的实例。" : "它连接后才能操作。"
    }
}

// MARK: - Sheets

/// Issues a pairing code: for a new device, or bound to a removed host so it can come back.
struct PairingSheet: View {
    let host: ManagedHost?
    let management: ManagementModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 16) {
            Text(host.map { "重新接入 \($0.name)" } ?? "接入设备或服务器").font(.title3.weight(.semibold))
            if let pairing = management.pairing {
                TimelineView(.periodic(from: .now, by: 1)) { timeline in
                    let left = max(0, Int(pairing.expiresAt.timeIntervalSince(timeline.date)))
                    VStack(spacing: 8) {
                        HStack(spacing: 6) {
                            ForEach(Array(pairing.code.enumerated()), id: \.offset) { _, letter in
                                Text(String(letter))
                                    .font(.system(size: 26, weight: .medium, design: .monospaced))
                                    .frame(width: 34, height: 46)
                                    .background(.quinary, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                                    .foregroundStyle(left == 0 ? .tertiary : .primary)
                            }
                        }
                        .textSelection(.enabled)
                        Text(left == 0 ? "已失效" : "\(left / 60):\(String(format: "%02d", left % 60)) 后失效，只能用一次")
                            .font(.callout).foregroundStyle(.secondary).monospacedDigit()
                    }
                }
            } else if let error = management.error {
                Text(error).foregroundStyle(.red)
            } else {
                ProgressView().frame(height: 72)
            }
            Text(host == nil
                 ? "在新设备的 Orisumi 中打开“设置 › 连接”输入它。服务器上，把它作为 --pairing-code 交给 Runtime。"
                 : "在这台机器上输入它，它会以原来的身份回来，继续发送本机还没发出去的数据。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                if let pairing = management.pairing {
                    Button("拷贝") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(pairing.code, forType: .string)
                    }
                }
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 400)
        .task { await management.issuePairing(host: host?.id) }
    }
}

/// The packages another host can install, read from that host's Runtime.
struct RemotePackageSheet: View {
    let host: ManagedHost
    let management: ManagementModel
    @Environment(\.dismiss) private var dismiss
    @State private var installing: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("安装到 \(host.name)").font(.title3.weight(.semibold))
            Group {
                if management.catalog.isEmpty {
                    VStack(spacing: 8) {
                        if management.busy {
                            ProgressView().controlSize(.small)
                            Text("正在读取它可以安装的 Observer").foregroundStyle(.secondary)
                        } else {
                            Text(management.error ?? "没有可以安装的 Observer").foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, minHeight: 96)
                } else {
                    VStack(spacing: 0) {
                        ForEach(management.catalog) { package in
                            row(package)
                            if package.id != management.catalog.last?.id { Divider().padding(.leading, 56) }
                        }
                    }
                }
            }
            .background(.quinary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            HStack {
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 480)
        .task { await management.execute(host: host.id, action: "catalog") }
    }

    private func row(_ package: CatalogPackage) -> some View {
        let same = management.instances.filter { $0.hostId == host.id && $0.instanceKey == package.id }
        let fresh = !same.isEmpty && same.allSatisfy(\.removed)
        let installed = package.installedVersion != nil && !fresh
        return HStack(spacing: 12) {
            ObserverIcon(packageId: package.id, size: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(package.name).font(.headline)
                Text(!package.compatible ? "不适用于这台机器"
                     : fresh ? "原来的实例已删除，安装后是一个新的程序实例"
                     : package.description ?? "版本 \(package.version)")
                    .foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer(minLength: 12)
            if installing == package.id, management.busy {
                ProgressView().controlSize(.small)
            } else if installed || (installing == package.id && management.operation?.state == "succeeded") {
                Text("已安装").foregroundStyle(.secondary)
            } else {
                Button(fresh ? "新建实例" : "安装") {
                    installing = package.id
                    Task { await management.execute(host: host.id, action: "install", package: package.id, newInstance: fresh) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(!package.compatible || management.busy)
            }
        }
        .padding(.horizontal, 12)
        .frame(minHeight: 60)
    }
}

// MARK: - Shared

struct InspectorTitle: View {
    let packageId: String
    let name: String
    let detail: String?

    var body: some View {
        HStack(spacing: 12) {
            ObserverIcon(packageId: packageId, size: 36)
            VStack(alignment: .leading, spacing: 2) {
                Text(name).font(.headline)
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary) }
            }
        }
    }
}

/// The instance's settings as a JSON object, saved as a whole.
struct SettingsSection: View {
    let settings: JSONValue
    let disabled: Bool
    let save: (JSONValue) -> Void
    @State private var text = ""

    var body: some View {
        let parsed = Self.parse(text)
        Section {
            TextEditor(text: $text)
                .font(.system(.callout, design: .monospaced))
                .frame(minHeight: 96)
                .scrollContentBackground(.hidden)
            HStack {
                Text(parsed == nil ? "要写成一个 JSON 对象" : "整个对象一起保存")
                    .font(.caption)
                    .foregroundStyle(parsed == nil ? .red : .secondary)
                Spacer()
                if text != Self.pretty(settings) {
                    Button("还原") { text = Self.pretty(settings) }
                }
                Button("保存") { if let parsed { save(parsed) } }
                    .disabled(parsed == nil || text == Self.pretty(settings) || disabled)
            }
        } header: {
            Text("设置")
        }
        .onAppear { text = Self.pretty(settings) }
        .onChange(of: settings) { text = Self.pretty(settings) }
    }

    static func pretty(_ value: JSONValue) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return (try? encoder.encode(value)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    }

    static func parse(_ text: String) -> JSONValue? {
        guard let value = try? JSONDecoder().decode(JSONValue.self, from: Data(text.utf8)), case .object = value else { return nil }
        return value
    }
}

/// How another host's state reads. A host that is not connected only has its last report, so its running state is unknown.
enum HostText {
    static func connection(_ host: ManagedHost) -> String {
        if host.revoked { return "已移除" }
        if host.connected { return "在线" }
        guard let seen = host.lastSeenAt else { return "还没连接过" }
        return "离线，\(seen.formatted(.relative(presentation: .named)))联系过"
    }

    static func state(_ host: ManagedHost, _ instance: ManagedInstance) -> String {
        guard host.connected, let state = instance.state else { return "状态未知" }
        return ["running": "运行中", "starting": "启动中", "stopping": "停止中", "stopped": "已停止",
                "waiting": "等待重启", "failed": "运行失败"][state] ?? "状态未知"
    }

    static func color(_ host: ManagedHost, _ instance: ManagedInstance) -> Color {
        guard host.connected, let state = instance.state else { return .secondary.opacity(0.5) }
        switch state {
        case "running": return .green
        case "failed": return .red
        case "stopped": return .secondary
        default: return .orange
        }
    }

    static func operation(_ operation: ManagementOperation) -> String {
        let action = ["update": "更新", "retry": "重试", "uninstall": "卸载", "install": "安装"][operation.action] ?? "操作"
        switch operation.state {
        case "queued", "delivered": return "正在\(action)…"
        case "succeeded": return "\(action)完成。"
        case "failed": return operation.error ?? "\(action)失败。"
        case "expired": return "设备没有响应，没有执行。"
        default: return "没有收到结果，上面是设备现在的状态。"
        }
    }
}

extension ManagedInstance {
    var state: String? { status?["state"]?.text }
    var version: String? { status?["version"]?.text }
    var pending: Int { Int(status?["delivery"]?["pending"]?.number ?? 0) }
    var problem: String? {
        status?["error"]?.text ?? status?["delivery"]?["rejection"]?["detail"]?.text ?? status?["delivery"]?["error"]?.text
    }
}
