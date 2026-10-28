import AppKit
import OrisumiClient
import SwiftUI

/// The Observer tab of Settings: what runs on this Mac, how sending goes, and installing, updating and removing.
struct ObserverSettings: View {
    let model: RuntimeModel
    @Bindable var navigation: Navigation
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Form {
            if let error = model.operationError, model.busy.isEmpty {
                Section {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.red)
                        .textSelection(.enabled)
                }
            }
            Section("已安装") {
                if model.installed.isEmpty {
                    Text("还没有安装 Observer。").foregroundStyle(.secondary)
                }
                ForEach(model.installed) { observer in
                    ObserverSettingsRow(model: model, observer: observer, show: show)
                }
            }
            if !model.uninstalled.isEmpty {
                Section {
                    ForEach(model.uninstalled) { observer in
                        ObserverSettingsRow(model: model, observer: observer, show: show)
                    }
                } header: {
                    Text("已卸载")
                } footer: {
                    Text("程序已移除，还没发送的数据继续发送。重新安装后沿用原来的身份和设置。")
                }
            }
        }
        .formStyle(.grouped)
        .frame(minHeight: 260, idealHeight: 380)
        .safeAreaInset(edge: .bottom, spacing: 0) {
            HStack {
                Text(ObserverText.runtime(model)).font(.callout).foregroundStyle(.secondary)
                Spacer()
                Button("添加 Observer…") { navigation.addingObserver = true }
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 16)
        }
        .sheet(isPresented: $navigation.addingObserver) {
            AddObserverSheet(model: model, open: show)
        }
    }

    /// Shows the Observer's view in the main window.
    private func show(_ packageId: String) {
        navigation.selection = packageId
        openWindow(id: MainView.windowId)
        NSApp.activate()
    }
}

struct ObserverSettingsRow: View {
    let model: RuntimeModel
    let observer: ObserverStatus
    let show: (String) -> Void
    @State private var confirmingUninstall = false

    var body: some View {
        let busy = model.busy.contains(observer.packageId)
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                ObserverIcon(packageId: observer.packageId, size: 28)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(observer.name)
                        if let version = observer.version {
                            Text(version).font(.caption).foregroundStyle(.secondary).monospacedDigit()
                        }
                    }
                    HStack(spacing: 5) {
                        Circle().fill(ObserverText.color(observer)).frame(width: 6, height: 6)
                        Text(ObserverText.state(observer))
                        Text("·").foregroundStyle(.tertiary)
                        Text(ObserverText.deliverySummary(observer.delivery))
                    }
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                    .help(ObserverText.delivery(observer.delivery))
                }
                Spacer(minLength: 8)
                if busy { ProgressView().controlSize(.small) }
                actions(busy: busy)
            }
            ForEach(ObserverText.problems(observer), id: \.self) { problem in
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .padding(.leading, 38)
            }
            if let diagnostic = observer.diagnostic {
                Label(diagnostic, systemImage: "info.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                    .textSelection(.enabled)
                    .padding(.leading, 38)
            }
        }
        .padding(.vertical, 2)
        .confirmationDialog("卸载 \(observer.name)？", isPresented: $confirmingUninstall) {
            Button("卸载", role: .destructive) { Task { await model.uninstall(observer.packageId) } }
        } message: {
            Text("程序会被移除。程序实例的身份、设置和本地数据保留，待发送的数据继续发送；重新安装后沿用原来的身份。")
        }
    }

    @ViewBuilder private func actions(busy: Bool) -> some View {
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
                .labelsHidden()
                .controlSize(.small)
                .disabled(busy)
                .help(observer.enabled ? "停用后不再记录，待发送的数据继续发送" : "启用后开始记录")
            Menu {
                Button("在窗口中查看") { show(observer.packageId) }
                Divider()
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
                .disabled(busy)
                .help("沿用卸载前的身份和设置")
        }
    }
}
