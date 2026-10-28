import Foundation
import Testing
@testable import OrisumiClient

struct ActivityTests {
    static let day = DateInterval(start: Date(timeIntervalSince1970: 1_792_857_600), duration: 86_400)
    static let code = [AppIdentifier(scheme: "macos.bundle-id", value: "com.microsoft.VSCode")]

    static func period(_ id: Int, _ name: String, from start: TimeInterval, to end: TimeInterval,
                       identifiers: [AppIdentifier] = [], active: Bool = false) -> Period {
        Period(id: UUID(uuidString: String(format: "019e1bbb-ec00-7000-8000-%012d", id))!, name: name, identifiers: identifiers,
               startAt: day.start + start, endAt: day.start + end, isActive: active)
    }

    @Test func clipsPeriodsToTheDay() {
        let yesterday = Self.period(1, "Safari", from: -7_200, to: -3_600)
        let acrossMidnight = Self.period(2, "Codex", from: -600, to: 600)
        #expect(yesterday.clipped(to: Self.day, now: Self.day.start + 700) == nil)
        #expect(acrossMidnight.clipped(to: Self.day, now: Self.day.start)?.duration == 600)
    }

    @Test func sumsUsagePerApplicationUpToTheLastConfirmation() {
        let periods = [
            Self.period(1, "Visual Studio Code", from: 0, to: 600, identifiers: Self.code),
            Self.period(2, "Code", from: 1_000, to: 1_300, identifiers: Self.code),
            Self.period(3, "Claude", from: 2_000, to: 2_010, active: true),
        ]
        let usage = Today.usage(periods, day: Self.day, now: Self.day.start + 3_000)
        // The active period counts until its last confirmation, not until now.
        #expect(usage.map(\.id) == ["com.microsoft.VSCode", "Claude"])
        #expect(usage.map(\.duration) == [900, 10])
        #expect(usage.first?.periods == 2)
        #expect(usage.first?.longest == 600)
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
