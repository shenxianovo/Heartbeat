import AppKit
import OrisumiClient
import SwiftUI

@main
struct OrisumiApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        MenuBarExtra {
            MenuContent(model: delegate.model, navigation: delegate.navigation)
        } label: {
            Image(nsImage: MenuBarIcon.image(for: delegate.model))
        }
        Window("Orisumi", id: MainView.windowId) {
            MainView(model: delegate.model, navigation: delegate.navigation)
        }
        .defaultSize(width: 1180, height: 760)
        .defaultLaunchBehavior(UserDefaults.standard.bool(forKey: "showWindow") ? .presented : .suppressed)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("设置…") { delegate.navigation.selection = .settings }
                    .keyboardShortcut(",")
            }
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = RuntimeModel(transport: RuntimeConnection(configuration: Preferences.runtimeConfiguration))
    let navigation = Navigation()
    private var termination: DispatchSourceSignal?
    private var polling: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        model.start()
        // The menu bar icon needs the Observers' state while the window is closed, so the list is read less often then.
        polling = Task { [model, navigation] in
            await model.refresh()
            await model.refreshCatalog()
            while !Task.isCancelled {
                try? await Task.sleep(for: navigation.windowVisible ? .seconds(2) : .seconds(15))
                await model.refresh()
            }
        }
        // SIGTERM (orisumi mac stop, kill) quits like the menu: Runtime stops the Observers first.
        signal(SIGTERM, SIG_IGN)
        let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        termination.setEventHandler {
            // terminate(_:) waits in a nested event loop; leave this queue block so Runtime can be stopped.
            RunLoop.main.perform { MainActor.assumeIsolated { NSApp.terminate(nil) } }
        }
        termination.resume()
        self.termination = termination
    }

    /// Waits for Runtime to stop the Observers and exit before quitting.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        polling?.cancel()
        Task {
            await model.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

/// What the main window shows.
@MainActor
@Observable
final class Navigation {
    enum Item: Hashable {
        case observer(String)
        case settings
    }

    var selection: Item?
    var windowVisible = false
}

/// Settings live in UserDefaults. `dataDirectory`, `runtime`, `catalog` and `showWindow` have no UI; pass them as
/// launch arguments in development.
enum Preferences {
    static let backendKey = "backend"
    static let webConsoleKey = "webConsole"
    static let defaultBackend = "http://localhost:8080/api/"
    static let defaultWebConsole = "http://localhost:8081/"

    static var backend: URL {
        RuntimeConfiguration.address(from: UserDefaults.standard.string(forKey: backendKey) ?? "")
            ?? URL(string: defaultBackend)!
    }

    static var webConsole: URL {
        RuntimeConfiguration.address(from: UserDefaults.standard.string(forKey: webConsoleKey) ?? "")
            ?? URL(string: defaultWebConsole)!
    }

    static var dataDirectory: URL {
        UserDefaults.standard.string(forKey: "dataDirectory").map { URL(filePath: $0, directoryHint: .isDirectory) }
            ?? URL.applicationSupportDirectory.appending(path: "Orisumi/Runtime", directoryHint: .isDirectory)
    }

    static func runtimeConfiguration() -> RuntimeConfiguration {
        let defaults = UserDefaults.standard
        let executable = defaults.string(forKey: "runtime").map { URL(filePath: $0) }
            ?? Bundle.main.bundleURL.appending(path: "Contents/Helpers/Runtime/Orisumi.Observer.Runtime")
        let bundled = Bundle.main.bundleURL.appending(path: "Contents/Resources/Observers", directoryHint: .isDirectory)
        let catalog = defaults.string(forKey: "catalog").flatMap { URL(string: $0) }
        return RuntimeConfiguration(executable: executable, dataDirectory: dataDirectory, backend: backend,
                                    bundled: bundled, catalog: catalog)
    }
}
