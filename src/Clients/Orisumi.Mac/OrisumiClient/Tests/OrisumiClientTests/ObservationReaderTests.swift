import Foundation
import Testing
@testable import OrisumiClient

/// Serves `read` pages and `versions` from tables, and counts version lookups.
@MainActor
final class FakeReader: RuntimeReading {
    var pages: [String?: String] = [:]
    var versions: [UUID: String] = [:]
    var expiredOnce = false
    /// When set, the backend read fails.
    var versionsError: String?
    var queries: [ReadQuery] = []
    var lookedUp: [UUID] = []

    func read<Content: Decodable & Sendable>(_ query: ReadQuery, as: Content.Type) async throws -> ReadResult<Content> {
        queries.append(query)
        if query.cursor != nil, expiredOnce {
            expiredOnce = false
            throw RuntimeError.remote(code: "conflict", message: "expired")
        }
        return try RuntimeJSON.decoder.decode(ReadResult<Content>.self, from: Data(pages[query.cursor]!.utf8))
    }

    func versions<Content: Decodable & Sendable>(instanceId: UUID, _ ids: [UUID], as: Content.Type) async throws
        -> VersionsResult<Content> {
        lookedUp += ids
        if let versionsError { throw RuntimeError.remote(code: "unavailable", message: versionsError) }
        let found = ids.compactMap { versions[$0] }
        let missing = ids.filter { versions[$0] == nil }.map { #""\#($0.uuidString)""# }
        let text = #"{"versions":[\#(found.joined(separator: ","))],"missing":[\#(missing.joined(separator: ","))]}"#
        return try RuntimeJSON.decoder.decode(VersionsResult<Content>.self, from: Data(text.utf8))
    }
}

@MainActor
struct ObservationReaderTests {
    static let instance = UUID(uuidString: "019e1bbb-ec00-7000-8000-0000000000f1")!
    static let codex = UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000201")!
    static let safari = UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000203")!
    static let deleted = UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000204")!
    static let application = UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000301")!

    static func observation(_ index: Int, _ content: UUID) -> String {
        #"{"versionId":"019e1bbb-ec00-7000-8000-00000000010\#(index)","entityId":"019e1bbb-ec00-7000-8000-00000000000\#(index)","content":{"observerVersionId":"019e1bbb-ec00-7000-8000-000000000101","contentVersionId":"\#(content.uuidString)","startAt":"2026-10-24T16:00:00Z","endAt":"2026-10-24T16:50:00.5Z"}}"#
    }

    static func reader() -> FakeReader {
        let reader = FakeReader()
        reader.pages = [
            nil: #"{"observations":[\#(observation(1, codex))],"next":"page-2"}"#,
            "page-2": #"{"observations":[\#(observation(2, safari)),\#(observation(3, deleted))],"next":null}"#,
        ]
        reader.versions = [
            codex: #"{"versionId":"\#(codex.uuidString)","entityId":"019e1bbb-ec00-7000-8000-0000000000c1","content":{"name":"Codex","localApplicationVersionId":"\#(application.uuidString)"}}"#,
            safari: #"{"versionId":"\#(safari.uuidString)","entityId":"019e1bbb-ec00-7000-8000-0000000000c3","content":{"name":"Safari","icon":{"digest":"sha256:abababababababababababababababababababababababababababababababab","mediaType":"image/png","size":3},"localApplicationVersionId":null}}"#,
            application: #"{"versionId":"\#(application.uuidString)","entityId":"019e1bbb-ec00-7000-8000-0000000000a1","content":{"identifiers":[{"scheme":"macos.bundle-id","value":"com.openai.codex"}]}}"#,
        ]
        return reader
    }

    @Test func readsTodaysPeriodsAcrossPagesThroughRuntime() async throws {
        let reader = Self.reader()
        let foreground = ForegroundReader()
        let today = try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        // The period whose content is missing is left out.
        #expect(today.items.map(\.name) == ["Codex", "Safari"])
        #expect(today.items.first?.bundleId == "com.openai.codex")
        #expect(today.items.map(\.icon) == [nil, "sha256:abababababababababababababababababababababababababababababababab"])
        #expect(today.items.first?.endAt == RuntimeJSON.parseDate("2026-10-24T16:50:00.5Z"))

        #expect(reader.queries.first?.resourceName == ForegroundReader.resourceName)
        #expect(reader.queries.first?.instanceId == Self.instance)

        // Every refresh checks the referenced versions again, so deletions on the backend show up.
        reader.lookedUp = []
        _ = try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(Set(reader.lookedUp) == [Self.codex, Self.safari, Self.deleted, Self.application])
    }

    @Test func forgetsCachedContentThatRuntimeReportsMissing() async throws {
        let reader = Self.reader()
        let foreground = ForegroundReader()
        let first = try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(first.items.map(\.name) == ["Codex", "Safari"])

        // The period content and the application were deleted on the backend after the first read.
        reader.versions[Self.codex] = nil
        reader.versions[Self.application] = nil
        let second = try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(second.items.map(\.name) == ["Safari"])
    }

    @Test func dependencyReadErrorIsReportedToTheView() async throws {
        let reader = Self.reader()
        let foreground = ForegroundReader()
        _ = try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        reader.versionsError = "offline"
        await #expect(throws: RuntimeError.self) {
            try await foreground.today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        }
    }

    @Test func recentObservationsForgetMissingContentAndReportErrors() async throws {
        let reader = Self.reader()
        let recent = RecentReader()
        _ = try await recent.recent(instanceId: Self.instance, now: .now, from: reader)
        reader.versions[Self.codex] = nil
        let deleted = try await recent.recent(instanceId: Self.instance, now: .now, from: reader)
        #expect(deleted.items.first?.content == nil)

        reader.versionsError = "offline"
        await #expect(throws: RuntimeError.self) {
            try await recent.recent(instanceId: Self.instance, now: .now, from: reader)
        }
    }

    @Test func startsOverWhenTheCursorExpired() async throws {
        let reader = Self.reader()
        reader.expiredOnce = true
        let today = try await ForegroundReader().today(instanceId: Self.instance, day: ActivityTests.day, from: reader)
        #expect(today.items.count == 2)
        #expect(reader.queries.map(\.cursor) == [nil, "page-2", nil, "page-2"])
    }

    @Test func readsRecentObservationsWithPlainContent() async throws {
        let reader = Self.reader()
        let recent = try await RecentReader().recent(instanceId: Self.instance, now: .now, from: reader)
        #expect(recent.items.count == 1)
        #expect(recent.items.first?.content?.compact == #"{"localApplicationVersionId":"\#(Self.application.uuidString)","name":"Codex"}"#)
    }
}
