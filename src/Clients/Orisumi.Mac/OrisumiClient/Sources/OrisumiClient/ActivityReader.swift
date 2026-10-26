import Foundation

/// Reads saved foreground periods. Tests replace it.
public protocol ActivitySource: Sendable {
    func periods(observerId: UUID, during day: DateInterval, backend: URL) async throws -> [Period]
}

/// Reads a day of foreground periods of one program instance from the API.
public actor ActivityReader: ActivitySource {
    private let session: URLSession
    /// Versions never change, so they are cached by backend and VersionId.
    private var cache: [String: Data] = [:]

    public init(session: URLSession = .shared) {
        self.session = session
    }

    public func periods(observerId: UUID, during day: DateInterval, backend: URL) async throws -> [Period] {
        var observations: [Version<ObservationContent>] = []
        var after: String?
        repeat {
            var components = URLComponents(url: backend.appending(path: "observations"), resolvingAgainstBaseURL: false)!
            let format = ISO8601DateFormatter()
            components.queryItems = [
                URLQueryItem(name: "from", value: format.string(from: day.start)),
                URLQueryItem(name: "to", value: format.string(from: day.end)),
                URLQueryItem(name: "resourceName", value: "foreground-application-periods"),
                URLQueryItem(name: "limit", value: "1000"),
            ] + (after.map { [URLQueryItem(name: "after", value: $0)] } ?? [])
            guard let data = try await get(components.url!) else { throw URLError(.badServerResponse) }
            let page = try HelperEvent.decoder.decode(Page.self, from: data)
            observations += page.versions
            after = page.next
        } while after != nil

        var periods: [Period] = []
        for observation in observations {
            let content = observation.content
            guard let startAt = content.startAt, let endAt = content.endAt,
                  let observer: Version<Ignored> = try await version(content.observerVersionId, backend),
                  observer.entityId == observerId,
                  let period: Version<PeriodContent> = try await version(content.contentVersionId, backend)
            else { continue }
            let application: Version<ApplicationContent>? = try await version(period.content.localApplicationVersionId, backend)
            periods.append(Period(id: observation.entityId, name: period.content.name,
                                  identifiers: application?.content.identifiers ?? [], startAt: startAt, endAt: endAt))
        }
        return periods
    }

    /// Nil when the version's entity was deleted.
    private func version<Content: Decodable>(_ id: UUID, _ backend: URL) async throws -> Version<Content>? {
        let key = backend.absoluteString + id.uuidString
        let data: Data
        if let cached = cache[key] {
            data = cached
        } else {
            guard let fetched = try await get(backend.appending(path: "versions/\(id.uuidString.lowercased())")) else { return nil }
            cache[key] = fetched
            data = fetched
        }
        return try HelperEvent.decoder.decode(Version<Content>.self, from: data)
    }

    private func get(_ url: URL) async throws -> Data? {
        let (data, response) = try await session.data(from: url)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 || status == 410 { return nil }
        guard status == 200 else { throw URLError(.badServerResponse) }
        return data
    }

    private struct Page: Decodable {
        var versions: [Version<ObservationContent>]
        var next: String?
    }

    private struct Version<Content: Decodable>: Decodable {
        var entityId: UUID
        var content: Content
    }

    private struct Ignored: Decodable {
        init(from decoder: any Decoder) {}
    }

    private struct ObservationContent: Decodable {
        var observerVersionId: UUID
        var contentVersionId: UUID
        var startAt: Date?
        var endAt: Date?
    }

    private struct PeriodContent: Decodable {
        var name: String?
        var localApplicationVersionId: UUID
    }

    private struct ApplicationContent: Decodable {
        var identifiers: [AppIdentifier]
    }
}
