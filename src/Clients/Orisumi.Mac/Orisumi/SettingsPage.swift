import AppKit
import OrisumiClient
import ServiceManagement
import SwiftUI

struct SettingsPage: View {
    let model: RuntimeModel
    @AppStorage(Preferences.backendKey) private var backend = Preferences.defaultBackend
    @AppStorage(Preferences.webConsoleKey) private var webConsole = Preferences.defaultWebConsole
    @State private var backendDraft = ""
    @State private var loginItem = LoginItem()

    var body: some View {
        Form {
            Section {
                TextField("API 地址", text: $backendDraft)
                if !backendValid {
                    Text("请输入 http 或 https 地址。").foregroundStyle(.red)
                }
                Button("应用") {
                    backend = backendDraft
                    Task { await model.restartRuntime() }
                }
                .disabled(!backendValid || backendDraft == backend)
            } header: {
                Text("后端")
            } footer: {
                Text("应用后重新启动 Runtime，启用的 Observer 随之重新开始采集。")
            }
            Section("Web 控制台") {
                TextField("地址", text: $webConsole)
            }
            Section {
                LabeledContent("数据目录") {
                    Text(Preferences.dataDirectory.path)
                        .textSelection(.enabled)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting([Preferences.dataDirectory]) }
            } header: {
                Text("本机数据")
            } footer: {
                Text("每个 Observer 的身份、设置和还没发送的数据保存在这里，卸载 Observer 后仍然保留。")
            }
            Section {
                Toggle("登录时启动", isOn: Binding(get: { loginItem.enabled }, set: { loginItem.set($0) }))
                if loginItem.status == .requiresApproval {
                    Text("在“系统设置 › 通用 › 登录项”中允许 Orisumi。")
                }
                if let error = loginItem.error {
                    Text(error).foregroundStyle(.red)
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            backendDraft = backend
            loginItem.refresh()
        }
    }

    private var backendValid: Bool { RuntimeConfiguration.address(from: backendDraft) != nil }
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
