import Foundation
import Testing
@testable import HeartbeatClient

struct HelperEventTests {
    @Test func parsesCheckpointWithDotNetTime() throws {
        let line = """
            {"type":"checkpoint","observerId":"019e1bbb-ec00-7000-8000-000000000010","localApplicationId":"019e1bbb-ec00-7000-8000-000000000011","contentId":"019e1bbb-ec00-7000-8000-000000000012","observationId":"019e1bbb-ec00-7000-8000-000000000013","startAt":"2026-10-25T06:00:00.123456+00:00","endAt":"2026-10-25T06:05:00+00:00","name":"Visual Studio Code"}
            """
        guard case .checkpoint(let checkpoint) = HelperEvent.parse(line) else {
            Issue.record("Expected a checkpoint")
            return
        }
        #expect(checkpoint.name == "Visual Studio Code")
        #expect(checkpoint.observationId == UUID(uuidString: "019e1bbb-ec00-7000-8000-000000000013"))
        #expect(checkpoint.startAt.timeIntervalSince1970 == 1_792_908_000.123)
        #expect(checkpoint.endAt.timeIntervalSince1970 == 1_792_908_300)
    }

    @Test func parsesDeliveryWithRejection() {
        let line = #"{"type":"delivery","pending":2,"drafts":1,"saved":40,"rejection":{"rule":"OBSERVATION-6","detail":"endAt precedes startAt"},"error":null}"#
        #expect(HelperEvent.parse(line) == .delivery(Delivery(pending: 2, drafts: 1, saved: 40,
            rejection: Rejection(rule: "OBSERVATION-6", detail: "endAt precedes startAt"))))
    }

    @Test(arguments: ["", "not json", #"{"type":"other"}"#, #"{"type":"delivery"}"#])
    func ignoresOtherLines(line: String) {
        #expect(HelperEvent.parse(line) == nil)
    }

    @Test func readsSevenFractionalDigitsAndOffsets() {
        #expect(HelperEvent.parseDate("2026-10-25T14:00:00.1234567+08:00")?.timeIntervalSince1970 == 1_792_908_000.123)
        #expect(HelperEvent.parseDate("2026-10-25T06:00:00.5Z")?.timeIntervalSince1970 == 1_792_908_000.5)
    }

    @Test func passesBackendAndDataDirectory() {
        let configuration = HelperConfiguration(
            executable: URL(filePath: "/helper"),
            backend: URL(string: "http://localhost:8080/api/")!,
            dataDirectory: URL(filePath: "/tmp/heartbeat-check"))
        #expect(configuration.arguments == [
            "--backend", "http://localhost:8080/api/",
            "--identity-file", "/tmp/heartbeat-check/identity.json",
            "--applications-file", "/tmp/heartbeat-check/local-applications.json",
        ])
    }

    @Test func acceptsOnlyHttpBackends() {
        #expect(HelperConfiguration.backend(from: " http://localhost:8080/api/ ") != nil)
        #expect(HelperConfiguration.backend(from: "ftp://localhost/api/") == nil)
        #expect(HelperConfiguration.backend(from: "localhost:8080") == nil)
    }
}
