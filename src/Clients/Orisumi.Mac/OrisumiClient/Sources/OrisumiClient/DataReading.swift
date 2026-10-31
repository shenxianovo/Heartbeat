import Foundation

/// Reading saved data, for views.
@MainActor
public protocol DataReading: AnyObject {
    func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws -> ReadResult<Content>
    func versions<Content: Decodable & Sendable>(_ ids: [UUID], as: Content.Type) async throws -> VersionsResult<Content>
}

/// One page of the Observations an instance is responsible for.
public struct ReadQuery: Equatable, Sendable {
    public var instanceId: UUID
    public var from: String
    public var to: String
    public var resourceName: String?
    public var limit: Int
    public var descending: Bool
    public var cursor: String?

    public init(instanceId: UUID, during interval: DateInterval, resourceName: String? = nil, limit: Int = 200,
                descending: Bool = false, cursor: String? = nil) {
        self.instanceId = instanceId
        self.from = RuntimeJSON.format(interval.start)
        self.to = RuntimeJSON.format(interval.end)
        self.resourceName = resourceName
        self.limit = limit
        self.descending = descending
        self.cursor = cursor
    }

    /// The path of `GET /observations`, relative to the API address.
    public var path: String {
        var items = [("observerEntityId", instanceId.uuidString.lowercased()), ("from", from), ("to", to), ("limit", String(limit))]
        if let resourceName { items.append(("resourceName", resourceName)) }
        if descending { items.append(("order", "desc")) }
        if let cursor { items.append(("after", cursor)) }
        // The offset in a time contains +, which a query reads as a space.
        var allowed = CharacterSet.urlQueryAllowed
        allowed.remove(charactersIn: "+&=")
        let query = items.map { "\($0)=\($1.addingPercentEncoding(withAllowedCharacters: allowed)!)" }.joined(separator: "&")
        return "observations?\(query)"
    }
}

/// Reads go to the API directly; Runtime only says how to connect.
extension RuntimeModel: DataReading {
    private struct Connection: Decodable {
        struct Api: Decodable { var url: URL; var credential: String? }
        var api: Api
    }
    private struct Problem: Decodable { var detail: String?; var title: String? }
    private struct VersionsRequest: Encodable { var versionIds: [UUID] }

    public func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws -> ReadResult<Content> {
        try decode(try await request(query.path))
    }

    public func versions<Content: Decodable & Sendable>(_ ids: [UUID], as: Content.Type) async throws -> VersionsResult<Content> {
        try decode(try await request("versions/read", body: try JSONEncoder().encode(VersionsRequest(versionIds: ids))))
    }

    /// The bytes of one binary content.
    public func content(digest: String) async throws -> Data {
        try await request("contents/\(digest)")
    }

    private func decode<Result: Decodable>(_ data: Data) throws -> Result {
        do { return try RuntimeJSON.decoder.decode(Result.self, from: data) } catch { throw RuntimeError.invalidResponse }
    }

    private func request(_ path: String, body: Data? = nil) async throws -> Data {
        let connection: Connection = try await call("connection", Empty())
        guard let credential = connection.api.credential else {
            throw RuntimeError.remote(code: "unavailable", message: "还没有接入后端")
        }
        var request = URLRequest(url: URL(string: path, relativeTo: connection.api.url)!)
        request.setValue("Bearer \(credential)", forHTTPHeaderField: "Authorization")
        if let body {
            request.httpMethod = "POST"
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw RuntimeError.invalidResponse }
        guard (200...299).contains(response.statusCode) else {
            let problem = try? JSONDecoder().decode(Problem.self, from: data)
            throw RuntimeError.remote(code: "unavailable", message: problem?.detail ?? problem?.title ?? "HTTP \(response.statusCode)")
        }
        return data
    }
}
