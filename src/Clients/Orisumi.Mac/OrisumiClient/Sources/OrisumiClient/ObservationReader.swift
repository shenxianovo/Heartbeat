import Foundation

/// The saved items read for a view.
public struct Snapshot<Item: Sendable>: Sendable {
    public var items: [Item]
    public init(items: [Item]) { self.items = items }
}

/// Reads all pages of one query, starting over once when the snapshot cursor expired.
@MainActor
func readAll<Content: Decodable & Sendable>(_ query: ReadQuery, from reader: RuntimeReading,
                                            as: Content.Type) async throws -> ReadResult<Content> {
    for attempt in 0..<2 {
        var query = query
        var items: [RuntimeVersion<Content>] = []
        do {
            repeat {
                let page = try await reader.read(query, as: Content.self)
                items += page.observations
                query.cursor = page.next
            } while query.cursor != nil
            return ReadResult(observations: items, next: nil)
        } catch let failure as RuntimeError where failure.code == "conflict" && attempt == 0 {
            continue
        }
    }
    throw RuntimeError.invalidResponse
}

/// Referenced contents are refreshed from the backend; missing versions are removed from the cache.
@MainActor
final class VersionCache<Content: Decodable & Sendable> {
    private(set) var contents: [UUID: Content] = [:]

    subscript(id: UUID) -> Content? { contents[id] }

    /// Reading failures propagate to the view.
    func refresh(_ ids: [UUID], instanceId: UUID, from reader: RuntimeReading) async throws {
        let unique = Array(Set(ids))
        for start in stride(from: 0, to: unique.count, by: 256) {
            let chunk = Array(unique[start..<min(start + 256, unique.count)])
            let result = try await reader.versions(instanceId: instanceId, chunk, as: Content.self)
            for version in result.versions { contents[version.versionId] = version.content }
            for id in result.missing { contents[id] = nil }
        }
        // Versions no longer referenced are dropped, so the cache stays the size of one refresh.
        let needed = Set(unique)
        contents = contents.filter { needed.contains($0.key) }
    }
}

/// Today's foreground application periods of one instance of the foreground state Observer.
@MainActor
public final class ForegroundReader {
    public static let resourceName = "foreground-application-periods"
    private let periods = VersionCache<PeriodContent>()
    private let applications = VersionCache<ApplicationContent>()

    public init() {}

    public func today(instanceId: UUID, day: DateInterval, from reader: RuntimeReading) async throws -> Snapshot<Period> {
        let result = try await readAll(ReadQuery(instanceId: instanceId, during: day, resourceName: Self.resourceName),
                                       from: reader, as: ObservationMembers.self)
        let contentIds = result.observations.map(\.content.contentVersionId)
        try await periods.refresh(contentIds, instanceId: instanceId, from: reader)
        let applicationIds = contentIds.compactMap { periods[$0]?.localApplicationVersionId }
        try await applications.refresh(applicationIds, instanceId: instanceId, from: reader)

        let items = result.observations.compactMap { item -> Period? in
            let observation = item.content
            // A period whose content was deleted is left out.
            guard let startAt = observation.startAt, let endAt = observation.endAt,
                  let content = periods[observation.contentVersionId] else { return nil }
            let application = content.localApplicationVersionId.flatMap { applications[$0] }
            return Period(id: item.entityId, name: content.name, identifiers: application?.identifiers ?? [],
                          icon: content.icon?.digest, startAt: startAt, endAt: endAt)
        }
        return Snapshot(items: items)
    }

    struct PeriodContent: Decodable, Sendable {
        var name: String?
        var icon: Binary?
        var localApplicationVersionId: UUID?
    }

    struct Binary: Decodable, Sendable {
        var digest: String
    }

    struct ApplicationContent: Decodable, Sendable {
        var identifiers: [AppIdentifier]
    }
}

/// A recent Observation of any Observer, with its content as plain JSON.
public struct RecentObservation: Equatable, Identifiable, Sendable {
    public var id: UUID
    public var startAt: Date?
    public var endAt: Date?
    public var content: JSONValue?
}

/// The latest Observations of one instance, for Observers without a dedicated view.
@MainActor
public final class RecentReader {
    private let contents = VersionCache<JSONValue>()

    public init() {}

    public func recent(instanceId: UUID, now: Date, days: Int = 7, limit: Int = 50,
                       from reader: RuntimeReading) async throws -> Snapshot<RecentObservation> {
        let interval = DateInterval(start: now.addingTimeInterval(-Double(days) * 86_400), end: now)
        let page = try await reader.read(ReadQuery(instanceId: instanceId, during: interval, limit: limit),
                                         as: ObservationMembers.self)
        try await contents.refresh(page.observations.map(\.content.contentVersionId),
                                               instanceId: instanceId, from: reader)
        let items = page.observations.map { item in
            RecentObservation(id: item.entityId, startAt: item.content.startAt,
                              endAt: item.content.endAt,
                              content: contents[item.content.contentVersionId])
        }
        return Snapshot(items: items)
    }
}
