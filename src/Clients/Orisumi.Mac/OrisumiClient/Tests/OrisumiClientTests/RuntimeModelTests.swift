import Foundation
import Testing
@testable import OrisumiClient

/// Answers requests from a table keyed by method and records what was sent.
@MainActor
final class FakeTransport: RuntimeTransport {
    var onExit: (RuntimeExit) -> Void = { _ in }
    var isRunning = false
    var starts = 0
    var requests: [(method: String, parameters: String)] = []
    var responses: [String: Result<String, RuntimeError>] = [:]

    func start() throws {
        starts += 1
        isRunning = true
    }

    func request(_ method: String, _ parameters: Data) async throws -> Data {
        requests.append((method, String(decoding: parameters, as: UTF8.self)))
        switch responses[method] ?? .success("{}") {
        case .success(let text): return Data(text.utf8)
        case .failure(let error): throw error
        }
    }

    func shutdown() async {
        guard isRunning else { return }
        isRunning = false
        onExit(.requested)
    }

    func crash() {
        isRunning = false
        onExit(.unexpected(status: 134, message: "boom"))
    }
}

@MainActor
struct RuntimeModelTests {
    static let instance = "019e1bbb-ec00-7000-8000-0000000000f1"
    static let list = """
        {"observers":[
          {"packageId":"foreground-state","name":"Foreground State","version":"0.1.0","instanceId":"\(instance)","installed":true,
           "enabled":true,"state":"running","error":null,"diagnostic":"Accessibility denied","delivery":{"pending":2,"drafts":1,"saved":5,"error":null,"rejection":null}},
          {"packageId":"old","name":"Old","version":null,"instanceId":"019e1bbb-ec00-7000-8000-0000000000f2","installed":false,
           "enabled":false,"state":"stopped","error":null,
           "delivery":{"pending":1,"drafts":0,"saved":0,"error":null,"rejection":{"rule":"VERSION-6","detail":"conflict"}}}
        ]}
        """

    @Test func readsObserversAndSplitsUninstalledOnes() async {
        let transport = FakeTransport()
        transport.responses["list"] = .success(Self.list)
        let model = RuntimeModel(transport: transport)
        model.start()
        await model.refresh()
        #expect(model.state == .running)
        #expect(model.installed.map(\.packageId) == ["foreground-state"])
        #expect(model.uninstalled.first?.delivery.rejection?.rule == "VERSION-6")
        #expect(model.observer("foreground-state")?.delivery.pending == 2)
        #expect(model.observer("foreground-state")?.diagnostic == "Accessibility denied")
    }

    @Test func sendsManagementRequestsAndRefreshesAfterwards() async {
        let transport = FakeTransport()
        transport.responses["list"] = .success(Self.list)
        transport.responses["catalog"] = .success("""
            {"packages":[{"id":"foreground-state","name":"Foreground State","description":null,"version":"0.2.0",
              "target":"osx-arm64","installedVersion":"0.1.0","compatible":true}]}
            """)
        let model = RuntimeModel(transport: transport)
        model.start()
        await model.setEnabled("foreground-state", false)
        #expect(transport.requests.contains { $0.method == "setEnabled" && $0.parameters.contains(#""enabled":false"#) })
        #expect(transport.requests.suffix(2).map(\.method) == ["list", "catalog"])
        #expect(model.update(for: "foreground-state")?.version == "0.2.0")
        #expect(model.busy.isEmpty)
    }

    @Test func reportsAFailedRequestWithoutStoppingRuntime() async {
        let transport = FakeTransport()
        transport.responses["install"] = .failure(.remote(code: "unavailable", message: "catalog unavailable"))
        transport.responses["list"] = .success(#"{"observers":[]}"#)
        let model = RuntimeModel(transport: transport)
        model.start()
        await model.install("example")
        #expect(model.operationError == "catalog unavailable")
        #expect(transport.isRunning)
    }

    @Test func restartsRuntimeAfterUnexpectedExitsThenStops() async throws {
        let transport = FakeTransport()
        transport.responses["list"] = .success(#"{"observers":[]}"#)
        let model = RuntimeModel(transport: transport, restartDelays: [.milliseconds(1), .milliseconds(1)])
        model.start()
        for _ in 0..<2 {
            transport.crash()
            #expect(model.state == .restarting)
            try await waitUntil { transport.isRunning }
        }
        transport.crash()
        #expect(model.state == .failed("boom"))
        #expect(transport.starts == 3)
        model.retryRuntime()
        #expect(transport.starts == 4)
    }

    @Test func doesNotRestartAfterShutdown() async throws {
        let transport = FakeTransport()
        let model = RuntimeModel(transport: transport, restartDelays: [.milliseconds(1)])
        model.start()
        await model.shutdown()
        #expect(model.state == .stopped)
        try await Task.sleep(for: .milliseconds(20))
        #expect(transport.starts == 1)
    }
}

@MainActor
func waitUntil(_ condition: () -> Bool) async throws {
    for _ in 0..<200 where !condition() { try await Task.sleep(for: .milliseconds(5)) }
    #expect(condition())
}
