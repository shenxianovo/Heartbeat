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
            MainView(model: delegate.model, management: delegate.management, navigation: delegate.navigation)
        }
        .defaultSize(width: 1080, height: 760)
        .defaultLaunchBehavior(UserDefaults.standard.bool(forKey: "showWindow") ? .presented : .suppressed)
        Settings {
            SettingsView(model: delegate.model, navigation: delegate.navigation)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = RuntimeModel(transport: RuntimeConnection(configuration: Preferences.runtimeConfiguration))
    let navigation = Navigation()
    let management = ManagementModel(address: Preferences.backend)
    private var termination: DispatchSourceSignal?
    private var polling: Task<Void, Never>?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Appearance(stored: UserDefaults.standard.string(forKey: Appearance.key)).apply()
        model.start()
        // The menu bar icon needs the Observers' state while the windows are closed, so the list is read less often then.
        polling = Task { [model, navigation] in
            let rejections = RejectionNotifier()
            await model.refresh()
            await model.refreshCatalog()
            rejections.update(model.observers)
            while !Task.isCancelled {
                try? await Task.sleep(for: navigation.windowVisible || navigation.settingsVisible ? .seconds(2) : .seconds(15))
                await model.refresh()
                rejections.update(model.observers)
            }
        }
        // SIGTERM (orisumi mac stop, kill) quits like the menu: Runtime stops the Observers first.
        signal(SIGTERM, SIG_IGN)
        let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        termination.setEventHandler {
            // terminate(_:) waits in a nested event loop; leave this queue block so Runtime can be stopped. Common modes
            // include the one the run loop is in while a sheet is open.
            RunLoop.main.perform(inModes: [.common]) { MainActor.assumeIsolated { AppDelegate.quit() } }
        }
        termination.resume()
        self.termination = termination
    }

    /// Quits like the menu item. An open sheet keeps terminate(_:) from going ahead, so sheets are closed first.
    static func quit() {
        for window in NSApp.windows {
            if let sheet = window.attachedSheet { window.endSheet(sheet) }
        }
        NSApp.terminate(nil)
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

/// Which window shows what. The app shows in the Dock while the main window or Settings is open.
@MainActor
@Observable
final class Navigation {
    /// What the main window shows: an Observer's data, this Mac's Observers, or another host's.
    enum Destination: Hashable {
        case view(String)
        case thisMac
        case host(UUID)
    }

    enum SettingsTab: Hashable {
        case general, connection
    }

    var selection: Destination?
    /// The Observer selected on this Mac's page, shown in the inspector.
    var managed: String?
    var addingObserver = false
    var settingsTab: SettingsTab = .general
    var windowVisible = false { didSet { updateActivation() } }
    var settingsVisible = false { didSet { updateActivation() } }

    private func updateActivation() {
        NSApp.setActivationPolicy(windowVisible || settingsVisible ? .regular : .accessory)
    }
}

/// Settings live in UserDefaults, including `appearance` (see `Appearance`). `dataDirectory`, `runtime`, `catalog` and
/// `showWindow` have no UI; pass them as launch arguments in development.
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
