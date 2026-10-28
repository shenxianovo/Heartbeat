import Foundation
import Testing
@testable import OrisumiClient

/// Drives a real Runtime with its bundled packages against a running API. Set ORISUMI_APP to a built Orisumi.app and
/// ORISUMI_BACKEND to the API address to run it, for example after `orisumi mac build` and `orisumi env up api --temp`.
@MainActor
@Suite(.serialized, .enabled(if: ProcessInfo.processInfo.environment["ORISUMI_APP"] != nil))
struct RuntimeIntegrationTests {
    static let packageId = "foreground-state"

    static func model(dataDirectory: URL) -> RuntimeModel {
        let environment = ProcessInfo.processInfo.environment
        let app = URL(filePath: environment["ORISUMI_APP"]!, directoryHint: .isDirectory)
        let configuration = RuntimeConfiguration(
            executable: app.appending(path: "Contents/Helpers/Runtime/Orisumi.Observer.Runtime"),
            dataDirectory: dataDirectory,
            backend: URL(string: environment["ORISUMI_BACKEND"] ?? "http://localhost:8080/api/")!,
            bundled: app.appending(path: "Contents/Resources/Observers", directoryHint: .isDirectory))
        return RuntimeModel(transport: RuntimeConnection { configuration })
    }

    static func waitFor(_ model: RuntimeModel, seconds: Double = 20,
                        _ condition: (ObserverStatus) -> Bool) async throws -> ObserverStatus {
        let deadline = Date.now.addingTimeInterval(seconds)
        while Date.now < deadline {
            await model.refresh()
            if let observer = model.observer(packageId), condition(observer) { return observer }
            try await Task.sleep(for: .milliseconds(200))
        }
        Issue.record("Timed out; last status: \(String(describing: model.observer(packageId)))")
        throw CancellationError()
    }

    @Test func enableUninstallReinstallKeepIdentityAndTodayReads() async throws {
        let directory = FileManager.default.temporaryDirectory.appending(path: "orisumi-integration-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        var model = Self.model(dataDirectory: directory)
        model.start()

        // The bundled package is installed on first start and collects right away.
        let running = try await Self.waitFor(model) { $0.state == .running }
        let instance = running.instanceId
        #expect(running.installed && running.enabled)

        // Today's periods read through Runtime include the active draft.
        let day = Today.interval(containing: .now)
        let reader = ForegroundReader()
        var periods: Snapshot<Period> = Snapshot(items: [])
        for _ in 0..<50 {
            periods = try await reader.today(instanceId: instance, day: day, from: model)
            if periods.items.contains { $0.isActive } { break }
            try await Task.sleep(for: .milliseconds(200))
        }
        #expect(periods.complete)
        #expect(periods.items.contains { $0.isActive })

        // Disabling stops the process; the period that was active is no longer a draft.
        let started = Date.now
        await model.setEnabled(Self.packageId, false)
        #expect(Date.now.timeIntervalSince(started) < 10)
        let disabled = try await Self.waitFor(model) { $0.state == .stopped && !$0.enabled }
        #expect(disabled.delivery.drafts == 0)
        let afterStop = try await reader.today(instanceId: instance, day: day, from: model)
        #expect(!afterStop.items.contains { $0.isActive })
        #expect(afterStop.items.count >= periods.items.count)

        await model.setEnabled(Self.packageId, true)
        _ = try await Self.waitFor(model) { $0.state == .running }

        // Uninstall keeps the instance; reinstall reuses it and collects again.
        await model.uninstall(Self.packageId)
        let uninstalled = try await Self.waitFor(model) { !$0.installed }
        #expect(uninstalled.instanceId == instance)
        await model.install(Self.packageId)
        let reinstalled = try await Self.waitFor(model) { $0.installed && $0.state == .running }
        #expect(reinstalled.instanceId == instance)
        #expect(model.operationError == nil)

        // Runtime restarts with the same identity and only runs what was enabled.
        await model.setEnabled(Self.packageId, false)
        await model.shutdown()
        model = Self.model(dataDirectory: directory)
        model.start()
        let restarted = try await Self.waitFor(model) { $0.state == .stopped }
        #expect(restarted.instanceId == instance)
        #expect(!restarted.enabled)
        await model.shutdown()
    }
}
