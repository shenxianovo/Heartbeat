import AppKit
import Testing
@testable import OrisumiClient

struct AppearanceTests {
    @Test func followsTheSystemUnlessLightOrDarkIsStored() {
        #expect(Appearance(stored: nil) == .system)
        #expect(Appearance(stored: "sepia") == .system)
        #expect(Appearance(stored: "light") == .light)
        #expect(Appearance(stored: "dark") == .dark)
    }

    @Test func mapsToAppKitAppearances() {
        #expect(Appearance.system.appearanceName == nil)
        #expect(Appearance.light.appearanceName == .aqua)
        #expect(Appearance.dark.appearanceName == .darkAqua)
    }
}
