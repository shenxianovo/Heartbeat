import AppKit
import OrisumiClient
import ServiceManagement
import SwiftUI

/// The Settings window: general preferences, the Observers on this Mac, and the addresses and data directory.
struct SettingsView: View {
    let model: RuntimeModel
    @Bindable var navigation: Navigation

    var body: some View {
        TabView(selection: $navigation.settingsTab) {
            Tab("通用", systemImage: "gearshape", value: .general) {
                GeneralSettings()
            }
            Tab("Observer", systemImage: "puzzlepiece.extension", value: .observers) {
                ObserverSettings(model: model, navigation: navigation)
            }
            Tab("高级", systemImage: "gearshape.2", value: .advanced) {
                AdvancedSettings(model: model)
            }
        }
        .frame(width: 620)
        .onAppear { navigation.settingsVisible = true }
        .onDisappear { navigation.settingsVisible = false }
    }
}

/// Opens Settings at a tab, in front of other apps.
@MainActor
struct SettingsOpener {
    let navigation: Navigation
    let openSettings: OpenSettingsAction

    func callAsFunction(_ tab: Navigation.SettingsTab, adding: Bool = false) {
        navigation.settingsTab = tab
        if adding { navigation.addingObserver = true }
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
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .frame(height: 230)
        .onAppear { loginItem.refresh() }
    }
}

struct AdvancedSettings: View {
    let model: RuntimeModel
    @AppStorage(Preferences.backendKey) private var backend = Preferences.defaultBackend
    @AppStorage(Preferences.webConsoleKey) private var webConsole = Preferences.defaultWebConsole
    @State private var backendDraft = ""

    var body: some View {
        Form {
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
                Text("连接")
            } footer: {
                Text("应用新的 API 地址会重新启动 Runtime，启用的 Observer 随之重新开始记录。")
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
            } header: {
                Text("本机数据")
            } footer: {
                Text("每个 Observer 的身份、设置和还没发送的数据保存在这里，卸载 Observer 后仍然保留。")
            }
        }
        .formStyle(.grouped)
        .scrollDisabled(true)
        .frame(height: 300)
        .onAppear { backendDraft = backend }
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
