import Foundation
import Testing
@testable import HeartbeatClient

struct ActivityTests {
    static let day = DateInterval(start: Date(timeIntervalSince1970: 1_792_857_600), duration: 86_400)
    static let code = [AppIdentifier(scheme: "macos.bundle-id", value: "com.microsoft.VSCode")]

    static func period(_ id: Int, _ name: String, from start: TimeInterval, to end: TimeInterval,
                       identifiers: [AppIdentifier] = [], active: Bool = false) -> Period {
        Period(id: UUID(uuidString: String(format: "019e1bbb-ec00-7000-8000-%012d", id))!, name: name, identifiers: identifiers,
               startAt: day.start + start, endAt: day.start + end, isActive: active)
    }

    @Test func mergesSavedLocalAndActiveNewestFirst() {
        let saved = [Self.period(1, "Safari", from: 3_600, to: 4_000)]
        let local = [Self.period(1, "Safari", from: 3_600, to: 3_900), Self.period(2, "Codex", from: 4_000, to: 5_000)]
        let active = Self.period(3, "Claude", from: 5_000, to: 5_100, active: true)
        let periods = Today.merge(saved: saved, local: local, active: active, day: Self.day, now: Self.day.start + 5_200)
        #expect(periods.map(\.name) == ["Claude", "Codex", "Safari"])
        // The saved version wins over the local copy of the same Observation.
        #expect(periods.last?.endAt == Self.day.start + 4_000)
    }

    @Test func keepsOnlyPeriodsInsideTheDay() {
        let yesterday = Self.period(1, "Safari", from: -7_200, to: -3_600)
        let acrossMidnight = Self.period(2, "Codex", from: -600, to: 600)
        let periods = Today.merge(saved: [yesterday, acrossMidnight], local: [], active: nil, day: Self.day, now: Self.day.start + 700)
        #expect(periods.map(\.name) == ["Codex"])
        #expect(acrossMidnight.clipped(to: Self.day, now: Self.day.start)?.duration == 600)
    }

    @Test func sumsUsagePerApplicationAndRunsTheActivePeriodUntilNow() {
        let periods = [
            Self.period(1, "Visual Studio Code", from: 0, to: 600, identifiers: Self.code),
            Self.period(2, "Code", from: 1_000, to: 1_300, identifiers: Self.code),
            Self.period(3, "Claude", from: 2_000, to: 2_010, active: true),
        ]
        let usage = Today.usage(periods, day: Self.day, now: Self.day.start + 3_000)
        #expect(usage.map(\.id) == ["Claude", "com.microsoft.VSCode"])
        #expect(usage.map(\.duration) == [1_000, 900])
        #expect(usage.last?.periods == 2)
        #expect(usage.last?.longest == 600)
    }
}

extension ActivityTests {
    @Test func drawsFromTheFirstActivityToNowOnWholeHours() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let periods = [Self.period(1, "Codex", from: 19 * 3_600 + 1_200, to: 20 * 3_600)]
        let range = Today.visibleRange(periods, day: Self.day, now: Self.day.start + 21 * 3_600 + 300, minimumHours: 3, calendar: calendar)
        #expect(range == DateInterval(start: Self.day.start + 19 * 3_600, end: Self.day.start + 22 * 3_600))
        #expect(Today.marks(in: range).count == 4)
        // A short morning still gets three hours, and a full day gets 6-hour marks.
        let morning = Today.visibleRange([], day: Self.day, now: Self.day.start + 600, minimumHours: 3, calendar: calendar)
        #expect(morning == DateInterval(start: Self.day.start, end: Self.day.start + 3 * 3_600))
        #expect(Today.marks(in: Self.day).count == 5)
    }
}

extension ActivityTests {
    @Test func splitsPeriodsIntoHoursAndGroupsTheRest() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let periods = [
            Self.period(1, "Codex", from: 3_000, to: 4_200, identifiers: [AppIdentifier(scheme: "macos.bundle-id", value: "codex")]),
            Self.period(2, "Safari", from: 4_200, to: 4_500),
            Self.period(3, "Notes", from: 4_500, to: 4_560),
        ]
        let slices = Today.hourly(periods, range: Self.day, now: Self.day.start + 5_000, keys: ["codex"], calendar: calendar)
        #expect(slices.map(\.duration) == [600, 600, 360])
        #expect(slices.map(\.key) == ["codex", "codex", nil])
        #expect(slices.map(\.hour) == [Self.day.start, Self.day.start + 3_600, Self.day.start + 3_600])
    }
}

@Suite(.serialized)
struct ActivityReaderTests {
    static let backend = URL(string: "http://heartbeat.test/api/")!
    static let observer = "019e1bbb-ec00-7000-8000-0000000000f1"

    /// VersionIds in the stubbed API: observer versions, period contents and the application.
    static let ownObserver = "019e1bbb-ec00-7000-8000-000000000101"
    static let otherObserver = "019e1bbb-ec00-7000-8000-000000000102"
    static let codex = "019e1bbb-ec00-7000-8000-000000000201"
    static let safari = "019e1bbb-ec00-7000-8000-000000000203"
    static let deleted = "019e1bbb-ec00-7000-8000-000000000204"
    static let application = "019e1bbb-ec00-7000-8000-000000000301"

    @Test func readsTheInstancesPeriodsAcrossPages() async throws {
        StubProtocol.reset([
            "/api/observations": """
                {"versions":[\(observation(1, Self.ownObserver, Self.codex))],"next":"page-2"}
                """,
            "/api/observations?after": """
                {"versions":[\(observation(2, Self.otherObserver, Self.codex)),\(observation(3, Self.ownObserver, Self.safari)),\(observation(4, Self.ownObserver, Self.deleted))],"next":null}
                """,
            "/api/versions/\(Self.ownObserver)": #"{"entityId":"\#(Self.observer)","content":{}}"#,
            "/api/versions/\(Self.otherObserver)": #"{"entityId":"019e1bbb-ec00-7000-8000-0000000000f2","content":{}}"#,
            "/api/versions/\(Self.codex)": #"{"entityId":"019e1bbb-ec00-7000-8000-0000000000c1","content":{"name":"Codex","localApplicationVersionId":"\#(Self.application)"}}"#,
            "/api/versions/\(Self.safari)": #"{"entityId":"019e1bbb-ec00-7000-8000-0000000000c3","content":{"name":"Safari","localApplicationVersionId":"\#(Self.application)"}}"#,
            "/api/versions/\(Self.application)": #"{"entityId":"019e1bbb-ec00-7000-8000-0000000000a1","content":{"identifiers":[{"scheme":"macos.bundle-id","value":"com.openai.codex"}]}}"#,
        ])
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        let reader = ActivityReader(session: URLSession(configuration: configuration))

        let periods = try await reader.periods(observerId: UUID(uuidString: Self.observer)!, during: ActivityTests.day, backend: Self.backend)
        // The other instance's period and the one whose content was deleted are left out.
        #expect(periods.map(\.name) == ["Codex", "Safari"])
        #expect(periods.first?.bundleId == "com.openai.codex")
        #expect(periods.first?.endAt == ActivityTests.day.start + 3_000.5)
        // Versions never change, so each is read once.
        #expect(StubProtocol.count("/api/versions/\(Self.ownObserver)") == 1)
        #expect(StubProtocol.count("/api/versions/\(Self.application)") == 1)
    }

    private func observation(_ index: Int, _ observer: String, _ content: String) -> String {
        #"{"entityId":"019e1bbb-ec00-7000-8000-00000000000\#(index)","content":{"observerVersionId":"\#(observer)","contentVersionId":"\#(content)","startAt":"2026-10-24T16:00:00Z","endAt":"2026-10-24T16:50:00.5Z"}}"#
    }
}

/// Answers requests from a table keyed by path; `?after` marks a later page. Unknown paths return 404.
final class StubProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var responses: [String: String] = [:]
    nonisolated(unsafe) private static var requests: [String] = []

    static func reset(_ responses: [String: String]) {
        lock.withLock {
            self.responses = responses
            requests = []
        }
    }

    static func count(_ path: String) -> Int { lock.withLock { requests.filter { $0 == path }.count } }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        let body: String? = Self.lock.withLock {
            Self.requests.append(url.path)
            return Self.responses[url.query?.contains("after=") == true ? url.path + "?after" : url.path]
        }
        let response = HTTPURLResponse(url: url, statusCode: body == nil ? 404 : 200, httpVersion: nil, headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data((body ?? "").utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
