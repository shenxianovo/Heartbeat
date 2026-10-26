import Foundation

/// One JSON line that the helper writes to standard output.
public enum HelperEvent: Equatable, Sendable {
    case checkpoint(Checkpoint)
    case delivery(Delivery)

    /// Returns nil for lines that are not helper events.
    public static func parse(_ line: String) -> HelperEvent? {
        let data = Data(line.utf8)
        guard let kind = try? decoder.decode(Kind.self, from: data) else { return nil }
        switch kind.type {
        case "checkpoint": return (try? decoder.decode(Checkpoint.self, from: data)).map(HelperEvent.checkpoint)
        case "delivery": return (try? decoder.decode(Delivery.self, from: data)).map(HelperEvent.delivery)
        default: return nil
        }
    }

    private struct Kind: Decodable { var type: String }

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            guard let date = parseDate(value) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Invalid time: \(value)"))
            }
            return date
        }
        return decoder
    }()

    /// .NET writes up to seven fractional digits; ISO8601DateFormatter reads three.
    static func parseDate(_ value: String) -> Date? {
        var text = value
        if let dot = text.firstIndex(of: "."),
           let end = text[dot...].dropFirst().firstIndex(where: { !$0.isNumber }) {
            let digits = text[text.index(after: dot)..<end]
            let millis = String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
            text.replaceSubrange(text.index(after: dot)..<end, with: millis)
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = text.contains(".")
            ? [.withInternetDateTime, .withFractionalSeconds]
            : [.withInternetDateTime]
        return formatter.date(from: text)
    }
}

/// The latest local confirmation of the active foreground period.
public struct Checkpoint: Decodable, Equatable, Sendable {
    public var observerId: UUID?
    public var observationId: UUID
    public var name: String?
    public var identifiers: [AppIdentifier]?
    public var startAt: Date
    public var endAt: Date

    public init(observerId: UUID? = nil, observationId: UUID, name: String?, identifiers: [AppIdentifier]? = nil,
                startAt: Date, endAt: Date) {
        self.observerId = observerId
        self.observationId = observationId
        self.name = name
        self.identifiers = identifiers
        self.startAt = startAt
        self.endAt = endAt
    }
}

/// The SDK's delivery status.
public struct Delivery: Decodable, Equatable, Sendable {
    public var pending: Int
    public var drafts: Int
    public var saved: Int
    public var rejection: Rejection?
    public var error: String?

    public init(pending: Int, drafts: Int, saved: Int, rejection: Rejection? = nil, error: String? = nil) {
        self.pending = pending
        self.drafts = drafts
        self.saved = saved
        self.rejection = rejection
        self.error = error
    }
}

public struct Rejection: Decodable, Equatable, Sendable {
    public var rule: String
    public var detail: String

    public init(rule: String, detail: String) {
        self.rule = rule
        self.detail = detail
    }
}
