import Foundation
import Observation

public struct RuntimeHostStatus: Decodable, Sendable {
    public var hostId: UUID?
    public var connection: String
}
public struct ManagedHost: Decodable, Identifiable, Sendable {
    public var hostId: UUID
    public var name: String
    public var kind: String
    public var revoked: Bool
    public var connected: Bool
    public var lastSeenAt: Date?
    public var id: UUID { hostId }
}
public struct ManagedInstance: Decodable, Identifiable, Sendable {
    public var entityId: UUID
    public var hostId: UUID
    public var packageId: String
    public var name: String
    public var enabled: Bool
    public var settings: JSONValue
    public var revision: Int64
    public var removed: Bool
    public var reportedAt: Date?
    public var status: JSONValue?
    public var id: UUID { entityId }
    public var synchronized: Bool { status?["appliedRevision"]?.number == Double(revision) && status?["pendingChanges"]?.number == 0 }
}
public struct ManagementOperation: Decodable, Sendable {
    public var operationId: UUID
    public var hostId: UUID
    public var action: String
    public var entityId: UUID?
    public var state: String
    public var error: String?
    public var result: JSONValue?
    public var pending: Bool { state == "queued" || state == "delivered" }
}
public struct PairingCode: Decodable, Sendable {
    public var code: String
    public var expiresAt: Date
    public var hostId: UUID?
}
extension JSONValue {
    public subscript(_ key: String) -> JSONValue? { if case .object(let fields) = self { fields[key] } else { nil } }
    public var text: String? { if case .string(let value) = self { value } else { nil } }
    public var number: Double? { if case .number(let value) = self { value } else { nil } }
}

@MainActor @Observable
public final class ManagementModel {
    public private(set) var hosts: [ManagedHost] = []
    public private(set) var instances: [ManagedInstance] = []
    public private(set) var error: String?
    public private(set) var pairing: PairingCode?
    public private(set) var operation: ManagementOperation?
    public private(set) var busy = false
    public private(set) var catalog: [CatalogPackage] = []
    private let address: URL
    private let session: URLSession
    public init(address: URL, session: URLSession = .shared) { self.address = address; self.session = session }
    private struct Hosts: Decodable { var hosts: [ManagedHost] }
    private struct Instances: Decodable { var instances: [ManagedInstance] }
    private struct Problem: Decodable { var detail: String?; var title: String? }
    private struct Empty: Decodable {}
    private func request<T: Decodable>(_ path: String, _ method: String = "GET", _ body: JSONValue? = nil) async throws -> T {
        var request = URLRequest(url: address.appending(path: path))
        request.httpMethod = method
        if let body { request.httpBody = try JSONEncoder().encode(body); request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw RuntimeError.invalidResponse }
        guard (200...299).contains(response.statusCode) else {
            let problem = try? JSONDecoder().decode(Problem.self, from: data)
            throw NSError(domain: "Management", code: response.statusCode, userInfo: [NSLocalizedDescriptionKey: problem?.detail ?? problem?.title ?? "HTTP \(response.statusCode)"])
        }
        return try RuntimeJSON.decoder.decode(T.self, from: response.statusCode == 204 ? Data("{}".utf8) : data)
    }
    public func refresh() async {
        do {
            let hostList: Hosts = try await request("hosts")
            let instanceList: Instances = try await request("observer-instances")
            hosts = hostList.hosts; instances = instanceList.instances; error = nil
        } catch { self.error = error.localizedDescription }
    }
    private func perform(_ work: () async throws -> Void) async {
        guard !busy else { return }
        busy = true; defer { busy = false }
        do { try await work(); error = nil; await refresh() } catch { self.error = error.localizedDescription }
    }
    public func issuePairing(host: UUID? = nil) async {
        pairing = nil
        await perform { pairing = try await request("pairings", "POST", .object(host.map { ["hostId": .string($0.uuidString)] } ?? [:])) }
    }
    public func rename(_ host: UUID, name: String) async {
        await perform { let _: ManagedHost = try await request("hosts/\(host)", "PATCH", .object(["name": .string(name)])) }
    }
    public func revoke(_ host: UUID) async {
        await perform { let _: Empty = try await request("hosts/\(host)/revoke", "POST") }
    }
    public func configure(_ instance: UUID, enabled: Bool? = nil, settings: JSONValue? = nil) async {
        var patch: [String: JSONValue] = [:]
        if let enabled { patch["enabled"] = .bool(enabled) }; if let settings { patch["settings"] = settings }
        let body = JSONValue.object(patch)
        await perform { let _: ManagedInstance = try await request("observer-instances/\(instance)", "PATCH", body) }
    }
    public func remove(_ instance: UUID) async {
        await perform { let _: Empty = try await request("observer-instances/\(instance)", "DELETE") }
    }
    public func execute(host: UUID, action: String, instance: UUID? = nil, package: String? = nil, newInstance: Bool = false) async {
        var body: [String: JSONValue] = ["action": .string(action), "newInstance": .bool(newInstance)]
        if let instance { body["entityId"] = .string(instance.uuidString) }; if let package { body["packageId"] = .string(package) }
        let payload = JSONValue.object(body)
        if action == "catalog" { catalog = [] }
        await perform {
            operation = try await request("hosts/\(host)/operations", "POST", payload)
            while let current = operation, current.pending {
                try await Task.sleep(for: .seconds(1))
                do { operation = try await request("hosts/\(host)/operations/\(current.operationId)") }
                catch { operation?.state = "unconfirmed"; throw error }
            }
            if let current = operation, current.action == "catalog", current.state == "succeeded", let result = current.result,
               case .array(let packages) = result["packages"] {
                catalog = try JSONDecoder().decode([CatalogPackage].self, from: JSONEncoder().encode(JSONValue.array(packages)))
            }
        }
    }
}
