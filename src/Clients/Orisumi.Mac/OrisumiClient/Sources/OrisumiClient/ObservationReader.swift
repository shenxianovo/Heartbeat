import Foundation

/// What a view read: the items, and whether the backend part was available.
public struct Snapshot<Item: Sendable>: Sendable {
    public var items: [Item]
    /// False when only local data could be read; statistics may then be incomplete.
    public var complete: Bool
    public var error: String?

    public init(items: [Item], complete: Bool = true, error: String? = nil) {
        self.items = items
        self.complete = complete
        self.error = error
    }
}

/// Reads all pages of one query, starting over once when the snapshot cursor expired.
@MainActor
func readAll<Content: Decodable & Sendable>(_ query: ReadQuery, from reader: RuntimeReading,
                                            as: Content.Type) async throws -> ReadResult<Content> {
    for attempt in 0..<2 {
        var query = query
        var items: [ReadResult<Content>.Item] = []
        var complete = true
        var error: String?
        do {
            repeat {
                let page = try await reader.read(query, as: Content.self)
                items += page.observations
                complete = complete && page.complete
                error = error ?? page.error
                query.cursor = page.next
            } while query.cursor != nil
            return ReadResult(observations: items, next: nil, complete: complete, error: error)
        } catch let failure as RuntimeError where failure.code == "conflict" && attempt == 0 {
            continue
        }
    }
    throw RuntimeError.invalidResponse
}

/// Contents of referenced versions. Every refresh asks Runtime for each version still needed: what it returns
/// replaces the cache, what it reports missing (deleted on the backend) is forgotten, and versions it could not read
/// keep their cached content while the snapshot is marked incomplete.
@MainActor
final class VersionCache<Content: Decodable & Sendable> {
    private(set) var contents: [UUID: Content] = [:]

    subscript(id: UUID) -> Content? { contents[id] }

    /// Returns Runtime's error when some versions could not be read.
    func refresh(_ ids: [UUID], instanceId: UUID, from reader: RuntimeReading) async throws -> String? {
        let unique = Array(Set(ids))
        var error: String?
        for start in stride(from: 0, to: unique.count, by: 256) {
            let chunk = Array(unique[start..<min(start + 256, unique.count)])
            let result = try await reader.versions(instanceId: instanceId, chunk, as: Content.self)
            for version in result.versions { contents[version.versionId] = version.content }
            for id in result.missing { contents[id] = nil }
            error = error ?? result.error
        }
        // Versions no longer referenced are dropped, so the cache stays the size of one refresh.
        let needed = Set(unique)
        contents = contents.filter { needed.contains($0.key) }
        return error
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
        let contentIds = result.observations.map(\.version.content.contentVersionId)
        let contentError = try await periods.refresh(contentIds, instanceId: instanceId, from: reader)
        let applicationIds = contentIds.compactMap { periods[$0]?.localApplicationVersionId }
        let applicationError = try await applications.refresh(applicationIds, instanceId: instanceId, from: reader)

        let items = result.observations.compactMap { item -> Period? in
            let observation = item.version.content
            // A period whose content was deleted is left out.
            guard let startAt = observation.startAt, let endAt = observation.endAt,
                  let content = periods[observation.contentVersionId] else { return nil }
            let application = content.localApplicationVersionId.flatMap { applications[$0] }
            return Period(id: item.version.entityId, name: content.name, identifiers: application?.identifiers ?? [],
                          icon: content.icon?.digest, startAt: startAt, endAt: endAt, isActive: item.state == .draft)
        }
        let error = result.error ?? contentError ?? applicationError
        return Snapshot(items: items, complete: result.complete && error == nil, error: error)
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
    public var state: LocalState
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
        let error = try await contents.refresh(page.observations.map(\.version.content.contentVersionId),
                                               instanceId: instanceId, from: reader)
        let items = page.observations.map { item in
            RecentObservation(id: item.version.entityId, startAt: item.version.content.startAt,
                              endAt: item.version.content.endAt, state: item.state,
                              content: contents[item.version.content.contentVersionId])
        }
        let readError = page.error ?? error
        return Snapshot(items: items, complete: page.complete && readError == nil, error: readError)
    }
}
