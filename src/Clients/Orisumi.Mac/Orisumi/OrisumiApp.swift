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
            Image(nsImage: MenuBarIcon.image(for: delegate.model.state))
        }
        Window("Orisumi", id: MainView.windowId) {
            MainView(model: delegate.model, navigation: delegate.navigation)
        }
        .defaultSize(width: 1080, height: 740)
        .defaultLaunchBehavior(UserDefaults.standard.bool(forKey: "showWindow") ? .presented : .suppressed)
        .commands {
            CommandGroup(replacing: .appSettings) {
                Button("设置…") { delegate.navigation.page = .settings }
                    .keyboardShortcut(",")
            }
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = ClientModel(configuration: Preferences.helperConfiguration)
    let navigation = Navigation()
    private var termination: DispatchSourceSignal?

    func applicationDidFinishLaunching(_ notification: Notification) {
        model.start()
        // SIGTERM (orisumi mac stop, kill) quits like the menu: the helper ends the active period first.
        signal(SIGTERM, SIG_IGN)
        let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        termination.setEventHandler {
            // terminate(_:) waits in a nested event loop; leave this queue block so the helper can be stopped.
            RunLoop.main.perform { MainActor.assumeIsolated { NSApp.terminate(nil) } }
        }
        termination.resume()
        self.termination = termination
    }

    /// Waits for the helper to end the active period before quitting.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task {
            await model.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }
}

/// The page shown in the main window.
@MainActor
@Observable
final class Navigation {
    enum Page: Hashable { case status, settings }
    var page: Page = .status
}

/// Settings live in UserDefaults. `dataDirectory`, `helper` and `showWindow` have no UI; pass them as launch arguments in development.
enum Preferences {
    static let backendKey = "backend"
    static let webConsoleKey = "webConsole"
    static let defaultBackend = "http://localhost:8080/api/"
    static let defaultWebConsole = "http://localhost:8081/"

    static var backend: URL {
        HelperConfiguration.backend(from: UserDefaults.standard.string(forKey: backendKey) ?? "")
            ?? URL(string: defaultBackend)!
    }

    static var webConsole: URL {
        HelperConfiguration.backend(from: UserDefaults.standard.string(forKey: webConsoleKey) ?? "")
            ?? URL(string: defaultWebConsole)!
    }

    static func helperConfiguration() -> HelperConfiguration {
        let defaults = UserDefaults.standard
        let executable = defaults.string(forKey: "helper").map { URL(filePath: $0) }
            ?? Bundle.main.bundleURL.appending(path: "Contents/Helpers/Orisumi.Observer.ForegroundState")
        let dataDirectory = defaults.string(forKey: "dataDirectory").map { URL(filePath: $0, directoryHint: .isDirectory) }
        return HelperConfiguration(executable: executable, backend: backend, dataDirectory: dataDirectory)
    }
}
