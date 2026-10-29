import Foundation

/// One identification clue of a local application, as stored in its content.
public struct AppIdentifier: Codable, Equatable, Hashable, Sendable {
    public var scheme: String
    public var value: String

    public init(scheme: String, value: String) {
        self.scheme = scheme
        self.value = value
    }
}

/// A foreground application period shown in the client, saved or still local.
/// An active period is an Observation whose local version is still a draft; its end is the last confirmation.
public struct Period: Equatable, Identifiable, Sendable {
    /// The Observation's EntityId.
    public var id: UUID
    public var name: String?
    public var identifiers: [AppIdentifier]
    public var startAt: Date
    /// The last confirmation.
    public var endAt: Date
    public var isActive: Bool

    public init(id: UUID, name: String?, identifiers: [AppIdentifier], startAt: Date, endAt: Date, isActive: Bool = false) {
        self.id = id
        self.name = name
        self.identifiers = identifiers
        self.startAt = startAt
        self.endAt = endAt
        self.isActive = isActive
    }

    public var bundleId: String? { identifiers.first { $0.scheme == "macos.bundle-id" }?.value }
    public var executablePath: String? { identifiers.first { $0.scheme == "macos.executable-path" }?.value }
    /// Groups periods of the same application.
    public var applicationKey: String { bundleId ?? executablePath ?? name ?? "" }

    /// The part of the period inside `day`. Durations count up to the last confirmation, never past it.
    public func clipped(to day: DateInterval, now: Date) -> DateInterval? {
        let start = max(startAt, day.start)
        let end = min(endAt, day.end)
        return start < end ? DateInterval(start: start, end: end) : nil
    }
}

/// Foreground time of one application within a day.
public struct Usage: Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String?
    public var identifiers: [AppIdentifier]
    public var duration: TimeInterval
    public var periods: Int
    public var longest: TimeInterval
}

/// Foreground time of one application within one hour. `key` is nil for applications grouped as "other".
public struct HourSlice: Equatable, Identifiable, Sendable {
    public var hour: Date
    public var key: String?
    public var name: String?
    public var duration: TimeInterval
    public var id: String { "\(hour.timeIntervalSince1970)|\(key ?? "")" }
}

public enum Today {
    public static func interval(containing date: Date, calendar: Calendar = .current) -> DateInterval {
        calendar.dateInterval(of: .day, for: date)!
    }

    /// Foreground time per application inside `day`, longest first.
    public static func usage(_ periods: [Period], day: DateInterval, now: Date) -> [Usage] {
        var usage: [String: Usage] = [:]
        for period in periods {
            guard let part = period.clipped(to: day, now: now) else { continue }
            var item = usage[period.applicationKey] ?? Usage(id: period.applicationKey, name: period.name,
                                                             identifiers: period.identifiers, duration: 0, periods: 0, longest: 0)
            item.duration += part.duration
            item.periods += 1
            item.longest = max(item.longest, part.duration)
            usage[period.applicationKey] = item
        }
        return usage.values.sorted { $0.duration != $1.duration ? $0.duration > $1.duration : $0.id < $1.id }
    }

    /// The hours of `day` worth drawing: from the first activity to now, on whole hours, at least `minimumHours` long.
    public static func visibleRange(_ periods: [Period], day: DateInterval, now: Date, minimumHours: Int = 6,
                                    calendar: Calendar = .current) -> DateInterval {
        let parts = periods.compactMap { $0.clipped(to: day, now: now) }
        let first = min(parts.map(\.start).min() ?? now, now)
        let last = max(parts.map(\.end).max() ?? now, now)
        var start = calendar.dateInterval(of: .hour, for: first)?.start ?? day.start
        var end = min(calendar.dateInterval(of: .hour, for: last)?.end ?? day.end, day.end)
        let minimum = TimeInterval(minimumHours * 3_600)
        if end.timeIntervalSince(start) < minimum { end = min(start + minimum, day.end) }
        if end.timeIntervalSince(start) < minimum { start = max(end - minimum, day.start) }
        return DateInterval(start: start, end: end)
    }

    /// Hour marks for `range`: at most five, a whole number of hours apart.
    public static func marks(in range: DateInterval) -> [Date] {
        let hours = Int((range.duration / 3_600).rounded())
        let step = [1, 2, 3, 4, 6].first { hours / $0 <= 4 } ?? 6
        return stride(from: 0, through: hours, by: step).map { range.start + TimeInterval($0 * 3_600) }
    }

    /// Splits periods into hours of `range`. Applications outside `keys` are summed under a nil key, after the named ones.
    public static func hourly(_ periods: [Period], range: DateInterval, now: Date, keys: [String],
                              calendar: Calendar = .current) -> [HourSlice] {
        var totals: [Date: [String?: (name: String?, duration: TimeInterval)]] = [:]
        for period in periods {
            guard let part = period.clipped(to: range, now: now) else { continue }
            let key = keys.contains(period.applicationKey) ? period.applicationKey : nil
            var start = part.start
            while start < part.end {
                guard let hour = calendar.dateInterval(of: .hour, for: start) else { break }
                let end = min(hour.end, part.end)
                totals[hour.start, default: [:]][key, default: (period.name, 0)].duration += end.timeIntervalSince(start)
                start = end
            }
        }
        let order = Dictionary(uniqueKeysWithValues: keys.enumerated().map { ($1, $0) })
        return totals.keys.sorted().flatMap { hour in
            totals[hour]!.map { HourSlice(hour: hour, key: $0.key, name: $0.value.name, duration: $0.value.duration) }
                .sorted { ($0.key.flatMap { order[$0] } ?? keys.count) < ($1.key.flatMap { order[$0] } ?? keys.count) }
        }
    }
}
