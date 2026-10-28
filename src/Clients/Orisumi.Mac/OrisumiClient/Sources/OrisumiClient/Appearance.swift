import AppKit

/// Orisumi's own appearance setting. It changes only Orisumi's windows, sheets and menus, never the system's.
public enum Appearance: String, CaseIterable, Identifiable, Sendable {
    case system, light, dark

    public static let key = "appearance"

    public var id: String { rawValue }

    /// A missing or unknown stored value follows the system.
    public init(stored: String?) {
        self = stored.flatMap(Appearance.init(rawValue:)) ?? .system
    }

    public var title: String {
        switch self {
        case .system: "跟随系统"
        case .light: "浅色"
        case .dark: "深色"
        }
    }

    /// The name for `NSApp.appearance`; nil follows the system.
    public var appearanceName: NSAppearance.Name? {
        switch self {
        case .system: nil
        case .light: .aqua
        case .dark: .darkAqua
        }
    }

    @MainActor
    public func apply(to application: NSApplication = .shared) {
        application.appearance = appearanceName.flatMap(NSAppearance.init(named:))
    }
}
