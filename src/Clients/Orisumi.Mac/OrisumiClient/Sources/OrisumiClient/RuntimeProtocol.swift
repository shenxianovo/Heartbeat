import Foundation

/// One registered Observer as `list` reports it.
public struct ObserverStatus: Decodable, Equatable, Identifiable, Sendable {
    public enum State: String, Decodable, Sendable {
        case starting, running, stopping, waiting, stopped, failed
    }

    public var packageId: String
    public var name: String
    public var version: String?
    public var instanceId: UUID
    /// False after uninstall while the instance still has data to send.
    public var installed: Bool
    public var enabled: Bool
    public var state: State
    /// The process's lifecycle failure, if any.
    public var error: String?
    /// The latest collection diagnostic the Observer wrote; the process may still be running.
    public var diagnostic: String?
    public var delivery: Delivery

    public var id: String { packageId }

    public init(packageId: String, name: String, version: String?, instanceId: UUID, installed: Bool = true,
                enabled: Bool = true, state: State = .running, error: String? = nil, diagnostic: String? = nil,
                delivery: Delivery = Delivery(pending: 0, drafts: 0, saved: 0)) {
        self.packageId = packageId
        self.name = name
        self.version = version
        self.instanceId = instanceId
        self.installed = installed
        self.enabled = enabled
        self.state = state
        self.error = error
        self.diagnostic = diagnostic
        self.delivery = delivery
    }
}

/// The delivery status of one program instance.
public struct Delivery: Decodable, Equatable, Sendable {
    public var pending: Int
    public var drafts: Int
    public var saved: Int
    public var error: String?
    /// Sending of this instance waits until its business code handles the rejected version.
    public var rejection: Rejection?

    public init(pending: Int, drafts: Int, saved: Int, error: String? = nil, rejection: Rejection? = nil) {
        self.pending = pending
        self.drafts = drafts
        self.saved = saved
        self.error = error
        self.rejection = rejection
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

/// A package that `catalog` offers for this device.
public struct CatalogPackage: Decodable, Equatable, Identifiable, Sendable {
    public var id: String
    public var name: String
    public var description: String?
    public var version: String
    public var target: String
    public var installedVersion: String?
    public var compatible: Bool

    public init(id: String, name: String, description: String? = nil, version: String, target: String,
                installedVersion: String? = nil, compatible: Bool = true) {
        self.id = id
        self.name = name
        self.description = description
        self.version = version
        self.target = target
        self.installedVersion = installedVersion
        self.compatible = compatible
    }

    /// The catalog has a newer version than the installed one.
    public var hasUpdate: Bool {
        guard let installedVersion else { return false }
        return PackageVersion(version) > PackageVersion(installedVersion)
    }
}

/// A three-part package version such as 0.1.0. Missing or non-numeric parts count as 0.
public struct PackageVersion: Comparable, Sendable {
    public var parts: [Int]

    public init(_ text: String) {
        parts = text.split(separator: ".").map { Int($0) ?? 0 }
        while parts.count < 3 { parts.append(0) }
    }

    public static func < (lhs: PackageVersion, rhs: PackageVersion) -> Bool {
        lhs.parts.lexicographicallyPrecedes(rhs.parts)
    }
}

/// A version as Runtime returns it. Only the fields the client reads are decoded.
public struct RuntimeVersion<Content: Decodable & Sendable>: Decodable, Sendable {
    public var versionId: UUID
    public var entityId: UUID
    public var content: Content
}

/// The state of an Observation on this device.
public enum LocalState: String, Decodable, Sendable {
    case draft, pending, saved, rejected
}

public struct ReadResult<Content: Decodable & Sendable>: Decodable, Sendable {
    public struct Item: Decodable, Sendable {
        public var version: RuntimeVersion<Content>
        public var state: LocalState
    }

    public var observations: [Item]
    public var next: String?
    /// False when the backend part could not be read; the result then holds local data only.
    public var complete: Bool
    public var error: String?
}

public struct VersionsResult<Content: Decodable & Sendable>: Decodable, Sendable {
    public var versions: [RuntimeVersion<Content>]
    public var missing: [UUID]
    public var error: String?
}

/// The members of an Observation version's content.
public struct ObservationMembers: Decodable, Sendable {
    public var observerVersionId: UUID
    public var contentVersionId: UUID
    public var startAt: Date?
    public var endAt: Date?
}

/// Any JSON value, for content whose type the client does not know.
public enum JSONValue: Codable, Equatable, Sendable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() { self = .null }
        else if let value = try? container.decode(Bool.self) { self = .bool(value) }
        else if let value = try? container.decode(Double.self) { self = .number(value) }
        else if let value = try? container.decode(String.self) { self = .string(value) }
        else if let value = try? container.decode([JSONValue].self) { self = .array(value) }
        else { self = .object(try container.decode([String: JSONValue].self)) }
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }

    /// Compact JSON with sorted keys.
    public var compact: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return (try? encoder.encode(self)).map { String(decoding: $0, as: UTF8.self) } ?? ""
    }
}

public enum RuntimeJSON {
    public static let decoder: JSONDecoder = {
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
    public static func parseDate(_ value: String) -> Date? {
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

    /// An ISO 8601 time with the local offset, as `read` expects.
    public static func format(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = .current
        return formatter.string(from: date)
    }
}
