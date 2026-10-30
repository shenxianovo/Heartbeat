import OrisumiClient
import SwiftUI

/// The Observers this Mac can install, from the bundled packages and the official catalog.
/// Each row says what the Observer records and offers the one thing that can be done with it.
struct AddObserverSheet: View {
    let model: RuntimeModel
    /// Shows an installed Observer in the window.
    let open: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var loaded = false

    /// Up to this many rows the sheet grows with its content; more scroll.
    private static let visibleRows = 5

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("添加 Observer").font(.title3.weight(.semibold))
                Text("安装后直接开始记录，可以随时停用。")
                    .foregroundStyle(.secondary)
            }
            content
            if let error = model.operationError, model.busy.isEmpty {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
                    .textSelection(.enabled)
            }
            HStack {
                if let note { Text(note).font(.callout).foregroundStyle(.secondary) }
                Spacer()
                Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 480)
        .task {
            await model.refreshCatalog()
            loaded = true
        }
    }

    @ViewBuilder private var content: some View {
        let packages = model.catalogByAction
        if packages.isEmpty {
            VStack(spacing: 8) {
                if !loaded {
                    ProgressView().controlSize(.small)
                } else if let error = model.catalogError {
                    Text("无法读取 Observer 列表").font(.headline)
                    Text(error).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    Button("重试") { Task { await model.refreshCatalog() } }
                } else {
                    Text("暂时没有可以安装的 Observer").foregroundStyle(.secondary)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 96)
            .background(.quinary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        } else {
            let rows = VStack(spacing: 0) {
                ForEach(packages) { package in
                    CatalogRow(model: model, package: package) {
                        dismiss()
                        open(package.id)
                    }
                    if package.id != packages.last?.id { Divider().padding(.leading, 56) }
                }
            }
            Group {
                if packages.count > Self.visibleRows {
                    ScrollView { rows }.frame(height: CatalogRow.height * CGFloat(Self.visibleRows))
                } else {
                    rows
                }
            }
            .background(.quinary, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        }
    }

    /// A line under the list when there is nothing to add, or when the official catalog could not be read.
    private var note: String? {
        if model.catalogError != nil, !model.catalog.isEmpty { return "这次没能刷新 Observer 列表，显示的是上次读到的。" }
        let packages = model.catalogByAction
        if !packages.isEmpty, packages.allSatisfy({ model.action(for: $0) == .open }) {
            return "目前没有其他可以安装的 Observer。"
        }
        return nil
    }
}

struct CatalogRow: View {
    static let height: CGFloat = 60
    let model: RuntimeModel
    let package: CatalogPackage
    let open: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            ObserverIcon(packageId: package.id, size: 32)
            VStack(alignment: .leading, spacing: 2) {
                Text(package.name).font(.headline)
                if let summary {
                    Text(summary).foregroundStyle(.secondary).lineLimit(2)
                }
            }
            Spacer(minLength: 12)
            action
        }
        .padding(.horizontal, 12)
        .frame(minHeight: Self.height)
    }

    /// The catalog's description, or what the client knows about Observers it has a view for.
    private var summary: String? {
        if let description = package.description, !description.isEmpty { return description }
        return package.id == ForegroundToday.packageId ? "记录哪个应用在前台，以及持续了多久" : nil
    }

    @State private var confirmingNewInstance = false

    @ViewBuilder private var action: some View {
        if model.busy.contains(package.id) {
            ProgressView().controlSize(.small)
        } else {
            switch model.action(for: package) {
            case .install:
                Button("安装") { Task { await model.install(package.id) } }
                    .buttonStyle(.borderedProminent)
            case .reinstall:
                Button("重新安装") { Task { await model.install(package.id) } }
                    .buttonStyle(.borderedProminent)
                    .help("沿用卸载前的身份和设置")
            case .newInstance:
                Button("新建实例") { confirmingNewInstance = true }
                    .confirmationDialog("创建新的程序实例？", isPresented: $confirmingNewInstance) {
                        Button("新建并安装") { Task { await model.install(package.id, newInstance: true) } }
                    } message: { Text("原来的实例已删除，新实例使用新的身份和数据目录。") }
            case .update(let version):
                Button("更新到 \(version)") { Task { await model.updatePackage(package.id) } }
                    .buttonStyle(.borderedProminent)
            case .open:
                HStack(spacing: 10) {
                    Text("已安装").foregroundStyle(.secondary)
                    Button("打开", action: open)
                }
            case .unavailable:
                Text("不适用于这台 Mac").foregroundStyle(.secondary)
            }
        }
    }
}
