import AppKit
import OrisumiClient
import ServiceManagement
import SwiftUI

/// The Settings window: how Orisumi behaves by default and where it connects. Using and managing Observers is in the main window.
struct SettingsView: View {
    let model: RuntimeModel
    @Bindable var navigation: Navigation

    var body: some View {
        TabView(selection: $navigation.settingsTab) {
            Tab("通用", systemImage: "gearshape", value: .general) {
                GeneralSettings()
            }
            Tab("连接", systemImage: "network", value: .connection) {
                ConnectionSettings(model: model)
            }
        }
        .frame(width: 560)
        .onAppear { navigation.settingsVisible = true }
        .onDisappear { navigation.settingsVisible = false }
    }
}

/// Opens Settings at a tab, in front of other apps.
@MainActor
struct SettingsOpener {
    let navigation: Navigation
    let openSettings: OpenSettingsAction

    func callAsFunction(_ tab: Navigation.SettingsTab) {
        navigation.settingsTab = tab
        NSApp.activate()
        openSettings()
    }
}

struct GeneralSettings: View {
    @AppStorage(Appearance.key) private var appearance = Appearance.system.rawValue
    @State private var loginItem = LoginItem()

    var body: some View {
        Form {
            Section {
                Picker("外观", selection: $appearance) {
                    ForEach(Appearance.allCases) { Text($0.title).tag($0.rawValue) }
                }
                .pickerStyle(.segmented)
                .onChange(of: appearance) { Appearance(stored: appearance).apply() }
            } footer: {
                Text("只改变 Orisumi 自己的窗口和菜单。")
            }
            Section {
                Toggle("登录时启动", isOn: Binding(get: { loginItem.enabled }, set: { loginItem.set($0) }))
                if loginItem.status == .requiresApproval {
                    Text("在“系统设置 › 通用 › 登录项”中允许 Orisumi。").foregroundStyle(.secondary)
                }
                if let error = loginItem.error {
                    Text(error).foregroundStyle(.red)
                }
            } footer: {
                Text("关闭窗口后 Orisumi 留在菜单栏，继续记录。")
            }
            Section {
                LabeledContent("数据目录") {
                    HStack(spacing: 6) {
                        Text(Preferences.dataDirectory.path)
                            .textSelection(.enabled)
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(.secondary)
                        Button("在 Finder 中显示", systemImage: "arrow.forward.circle.fill") {
                            NSWorkspace.shared.activateFileViewerSelecting([Preferences.dataDirectory])
                        }
                        .labelStyle(.iconOnly)
                        .buttonStyle(.borderless)
                        .help("在 Finder 中显示")
                    }
                }
            } footer: {
                Text("每个 Observer 的身份、设置和还没发送的数据保存在这里，卸载后仍然保留。")
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { loginItem.refresh() }
    }
}

/// Where this Mac connects: whether it has joined, and the API and Web console addresses.
struct ConnectionSettings: View {
    let model: RuntimeModel
    @AppStorage(Preferences.backendKey) private var backend = Preferences.defaultBackend
    @AppStorage(Preferences.webConsoleKey) private var webConsole = Preferences.defaultWebConsole
    @State private var backendDraft = ""
    @State private var code = ""
    @State private var name = Host.current().localizedName ?? "我的 Mac"

    var body: some View {
        Form {
            Section {
                LabeledContent("状态") {
                    HStack(spacing: 6) {
                        Circle().fill(connection.color).frame(width: 7, height: 7)
                        Text(connection.text)
                    }
                }
                if joinable {
                    TextField("配对码", text: $code)
                        .font(.body.monospaced())
                        .onChange(of: code) { code = String(code.uppercased().filter { $0.isLetter || $0.isNumber }.prefix(8)) }
                    TextField("名称", text: $name)
                    HStack {
                        Spacer()
                        if model.busy.contains("pairing") { ProgressView().controlSize(.small) }
                        Button(model.host?.connection == "revoked" ? "重新接入" : "接入") {
                            Task { await model.pair(code: code, name: name) }
                        }
                        .disabled(code.count != 8 || name.isEmpty || model.busy.contains("pairing"))
                        .keyboardShortcut(.defaultAction)
                    }
                    if let error = model.operationError, !model.busy.contains("pairing") {
                        Text(error).foregroundStyle(.red).textSelection(.enabled)
                    }
                }
            } header: {
                Text("这台 Mac")
            } footer: {
                if joinable {
                    Text("在已接入的设备上，或在 Web 控制台的“托管”页签发配对码。接入后，其他设备也能看到和管理这台 Mac 上的 Observer。")
                }
            }
            Section {
                LabeledContent("API 地址") {
                    HStack {
                        TextField("API 地址", text: $backendDraft)
                            .labelsHidden()
                            .onSubmit(apply)
                        Button("应用", action: apply)
                            .disabled(!backendValid || backendDraft == backend)
                    }
                }
                if !backendValid {
                    Text("请输入 http 或 https 地址。").foregroundStyle(.red)
                }
                TextField("Web 控制台", text: $webConsole)
            } header: {
                Text("地址")
            } footer: {
                Text("应用新的 API 地址会重新启动 Runtime，启用的 Observer 随之重新开始记录。")
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear { backendDraft = backend }
    }

    private var joinable: Bool { model.host?.hostId == nil || model.host?.connection == "revoked" }

    private var connection: (text: String, color: Color) {
        switch model.host?.connection {
        case "connected": ("已接入，在线", .green)
        case "offline": ("已接入，暂时连不上", .orange)
        case "revoked": ("已被移除，重新接入后继续同步", .red)
        default: ("还没有接入，只在本机记录", .secondary)
        }
    }

    private var backendValid: Bool { RuntimeConfiguration.address(from: backendDraft) != nil }

    private func apply() {
        guard backendValid, backendDraft != backend else { return }
        backend = backendDraft
        Task { await model.restartRuntime() }
    }
}

@MainActor
@Observable
final class LoginItem {
    private(set) var status = SMAppService.mainApp.status
    private(set) var error: String?

    var enabled: Bool { status == .enabled || status == .requiresApproval }

    func set(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            error = nil
        } catch {
            self.error = error.localizedDescription
        }
        refresh()
    }

    func refresh() {
        status = SMAppService.mainApp.status
    }
}
