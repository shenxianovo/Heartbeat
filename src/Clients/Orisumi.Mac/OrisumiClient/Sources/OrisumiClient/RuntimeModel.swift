import Foundation
import Observation

/// What the menu and window show: whether Runtime runs, the registered Observers and what can be installed.
@MainActor
@Observable
public final class RuntimeModel {
    public enum State: Equatable, Sendable {
        case starting
        case running
        /// Waiting before restarting Runtime after an unexpected exit.
        case restarting
        case failed(String)
        case stopped
    }

    public private(set) var state: State = .stopped
    public private(set) var observers: [ObserverStatus] = []
    public private(set) var catalog: [CatalogPackage] = []
    public private(set) var catalogError: String?
    /// Packages with an install, update, uninstall or enable request in flight.
    public private(set) var busy: Set<String> = []
    /// The last failed management request, shown until the next one succeeds.
    public private(set) var operationError: String?

    private let transport: RuntimeTransport
    private let restartDelays: [Duration]
    private let stableAfter: Duration
    private var failures = 0
    private var restart: Task<Void, Never>?
    private var stable: Task<Void, Never>?
    private var stopping = false

    public init(transport: RuntimeTransport, restartDelays: [Duration] = [.seconds(1), .seconds(2), .seconds(4)],
                stableAfter: Duration = .seconds(60)) {
        self.transport = transport
        self.restartDelays = restartDelays
        self.stableAfter = stableAfter
        transport.onExit = { [weak self] exit in self?.exited(exit) }
    }

    public var installed: [ObserverStatus] { observers.filter(\.installed) }
    public var uninstalled: [ObserverStatus] { observers.filter { !$0.installed } }

    public func observer(_ packageId: String) -> ObserverStatus? {
        observers.first { $0.packageId == packageId }
    }

    public func update(for packageId: String) -> CatalogPackage? {
        catalog.first { $0.id == packageId && $0.hasUpdate && $0.compatible }
    }

    // MARK: Runtime process

    public func start() {
        restart?.cancel()
        restart = nil
        guard !transport.isRunning else { return }
        stopping = false
        do {
            try transport.start()
            state = .starting
            Task { await refresh() }
            stable?.cancel()
            stable = Task { [stableAfter] in
                try? await Task.sleep(for: stableAfter)
                guard !Task.isCancelled else { return }
                self.failures = 0
            }
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// The user asks to try again after Runtime failed.
    public func retryRuntime() {
        failures = 0
        start()
    }

    public func shutdown() async {
        stopping = true
        restart?.cancel()
        stable?.cancel()
        await transport.shutdown()
        state = .stopped
    }

    /// Applies new settings by starting Runtime again.
    public func restartRuntime() async {
        await shutdown()
        failures = 0
        start()
    }

    private func exited(_ exit: RuntimeExit) {
        stable?.cancel()
        observers = observers.map { var observer = $0; observer.state = .stopped; return observer }
        guard case .unexpected(let status, let message) = exit, !stopping else {
            state = .stopped
            return
        }
        let reason = message ?? "Runtime 已退出，状态码 \(status)"
        guard failures < restartDelays.count else {
            state = .failed(reason)
            return
        }
        let delay = restartDelays[failures]
        failures += 1
        state = .restarting
        restart = Task {
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            self.start()
        }
    }

    // MARK: Requests

    public func refresh() async {
        guard transport.isRunning else { return }
        do {
            let list: ObserverList = try await call("list", Empty())
            observers = list.observers
            if state == .starting { state = .running }
        } catch {
            if transport.isRunning { state = .failed(error.localizedDescription) }
        }
    }

    public func refreshCatalog() async {
        guard transport.isRunning else { return }
        do {
            let result: CatalogList = try await call("catalog", Empty())
            catalog = result.packages
            catalogError = nil
        } catch {
            catalogError = error.localizedDescription
        }
    }

    public func install(_ packageId: String) async {
        await manage(packageId) { try await self.send("install", PackageRequest(packageId: packageId)) }
    }

    public func updatePackage(_ packageId: String) async {
        await manage(packageId) { try await self.send("update", PackageRequest(packageId: packageId)) }
    }

    public func uninstall(_ packageId: String) async {
        await manage(packageId) { try await self.send("uninstall", PackageRequest(packageId: packageId)) }
    }

    public func retry(_ packageId: String) async {
        await manage(packageId) { try await self.send("retry", PackageRequest(packageId: packageId)) }
    }

    public func setEnabled(_ packageId: String, _ enabled: Bool) async {
        await manage(packageId) {
            try await self.send("setEnabled", EnableRequest(packageId: packageId, enabled: enabled))
        }
    }

    private func manage(_ packageId: String, _ operation: @escaping () async throws -> Void) async {
        guard !busy.contains(packageId) else { return }
        busy.insert(packageId)
        defer { busy.remove(packageId) }
        do {
            try await operation()
            operationError = nil
        } catch {
            operationError = error.localizedDescription
        }
        await refresh()
        await refreshCatalog()
    }

    private func send(_ method: String, _ parameters: some Encodable) async throws {
        let _: Ignored = try await call(method, parameters)
    }

    private func call<Result: Decodable>(_ method: String, _ parameters: some Encodable) async throws -> Result {
        let data = try await transport.request(method, try JSONEncoder().encode(parameters))
        do { return try RuntimeJSON.decoder.decode(Result.self, from: data) } catch { throw RuntimeError.invalidResponse }
    }

    private struct Empty: Encodable {}
    private struct Ignored: Decodable { init(from decoder: any Decoder) {} }
    private struct PackageRequest: Encodable { var packageId: String }
    private struct EnableRequest: Encodable { var packageId: String; var enabled: Bool }
    private struct ObserverList: Decodable { var observers: [ObserverStatus] }
    private struct CatalogList: Decodable { var packages: [CatalogPackage] }
    private struct VersionsRequest: Encodable { var instanceId: UUID; var versionIds: [UUID] }
}

/// Reading through Runtime, for views.
@MainActor
public protocol RuntimeReading: AnyObject {
    func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws -> ReadResult<Content>
    func versions<Content: Decodable & Sendable>(instanceId: UUID, _ ids: [UUID], as: Content.Type) async throws
        -> VersionsResult<Content>
}

public struct ReadQuery: Encodable, Equatable, Sendable {
    public var instanceId: UUID
    public var from: String
    public var to: String
    public var resourceName: String?
    public var limit: Int
    public var cursor: String?

    public init(instanceId: UUID, during interval: DateInterval, resourceName: String? = nil, limit: Int = 200,
                cursor: String? = nil) {
        self.instanceId = instanceId
        self.from = RuntimeJSON.format(interval.start)
        self.to = RuntimeJSON.format(interval.end)
        self.resourceName = resourceName
        self.limit = limit
        self.cursor = cursor
    }
}

extension RuntimeModel: RuntimeReading {
    public func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws
        -> ReadResult<Content> {
        try await call("read", query)
    }

    public func versions<Content: Decodable & Sendable>(instanceId: UUID, _ ids: [UUID], as: Content.Type) async throws
        -> VersionsResult<Content> {
        try await call("versions", VersionsRequest(instanceId: instanceId, versionIds: ids))
    }
}
