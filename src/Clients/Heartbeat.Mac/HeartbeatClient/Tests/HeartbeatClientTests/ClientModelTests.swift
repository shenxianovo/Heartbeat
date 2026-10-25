import Foundation
import Testing
@testable import HeartbeatClient

/// Drives ClientModel with shell scripts that stand in for the helper.
@MainActor
struct ClientModelTests {
    static let checkpoint = #"{"type":"checkpoint","observationId":"019e1bbb-ec00-7000-8000-000000000013","startAt":"2026-10-25T06:00:00+00:00","endAt":"2026-10-25T06:00:05+00:00","name":"Codex"}"#
    static let delivery = #"{"type":"delivery","pending":1,"drafts":1,"saved":5,"rejection":null,"error":null}"#

    @Test func runsUntilPausedAndReceivesEvents() async throws {
        let marker = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        let model = try model(script: """
            trap 'echo stopped > "\(marker.path)"; exit 130' INT
            echo '\(Self.checkpoint)'
            echo '\(Self.delivery)'
            while true; do sleep 0.05; done
            """)
        model.start()
        #expect(model.state == .running)
        try await until { model.checkpoint != nil && model.delivery != nil }
        #expect(model.checkpoint?.name == "Codex")
        #expect(model.delivery?.pending == 1)

        await model.pause()
        #expect(model.state == .paused)
        #expect(model.checkpoint == nil)
        // The helper received SIGINT, so it could end the active period itself.
        #expect(FileManager.default.fileExists(atPath: marker.path))
    }

    @Test func keepsEndedPeriodsNewestFirst() async throws {
        let second = Self.checkpoint
            .replacingOccurrences(of: "000000000013", with: "000000000014")
            .replacingOccurrences(of: "Codex", with: "Safari")
        let model = try model(script: """
            trap 'exit 130' INT
            echo '\(Self.checkpoint)'
            echo '\(second)'
            while true; do sleep 0.05; done
            """)
        model.start()
        try await until { model.checkpoint?.name == "Safari" }
        #expect(model.periods.map(\.name) == ["Codex"])

        await model.pause()
        #expect(model.periods.map(\.name) == ["Safari", "Codex"])
    }

    @Test func readsSavedPeriodsOfTheInstanceFromTheCheckpoints() async throws {
        let observer = "019e1bbb-ec00-7000-8000-0000000000f1"
        let checkpoint = Self.checkpoint.replacingOccurrences(of: #""type":"checkpoint","#,
            with: #""type":"checkpoint","observerId":"\#(observer)","#)
        let source = FakeSource()
        let model = try model(script: """
            trap 'exit 130' INT
            echo '\(checkpoint)'
            while true; do sleep 0.05; done
            """, source: source)
        model.start()
        // The first checkpoint names the instance, and the model reads its saved periods.
        try await until { !model.saved.isEmpty }
        #expect(await source.requested.allSatisfy { $0 == UUID(uuidString: observer)! })
        #expect(model.saved.map(\.name) == ["Safari"])
        await model.shutdown()
    }

    @Test func reportsTheLastErrorLineWhenTheHelperExits() async throws {
        let model = try model(script: """
            echo 'Opening the state file' >&2
            echo 'The state file is locked by another process.' >&2
            exit 1
            """)
        model.start()
        try await until { model.state != .running }
        #expect(model.state == .failed("The state file is locked by another process."))
    }

    @Test func restartRunsTheHelperAgain() async throws {
        let count = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        let model = try model(script: """
            echo run >> "\(count.path)"
            trap 'exit 130' INT
            while true; do sleep 0.05; done
            """)
        model.start()
        try await until { (try? String(contentsOf: count, encoding: .utf8)) == "run\n" }
        await model.restart()
        try await until { (try? String(contentsOf: count, encoding: .utf8)) == "run\nrun\n" }
        #expect(model.state == .running)
        await model.shutdown()
    }

    @Test func failsWhenTheHelperIsMissing() {
        let model = ClientModel {
            HelperConfiguration(executable: URL(filePath: "/nonexistent/helper"), backend: URL(string: "http://localhost/")!)
        }
        model.start()
        guard case .failed = model.state else {
            Issue.record("Expected a failure, got \(model.state)")
            return
        }
    }

    private func model(script: String, source: ActivitySource = FakeSource()) throws -> ClientModel {
        let url = FileManager.default.temporaryDirectory.appending(path: "helper-\(UUID().uuidString).sh")
        try ("#!/bin/sh\n" + script + "\n").write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return ClientModel(source: source) {
            HelperConfiguration(executable: url, backend: URL(string: "http://localhost:8080/api/")!)
        }
    }

    private func until(_ condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now + .seconds(5)
        while !condition() {
            guard ContinuousClock.now < deadline else {
                Issue.record("Timed out")
                return
            }
            try await Task.sleep(for: .milliseconds(20))
        }
    }
}

actor FakeSource: ActivitySource {
    private(set) var requested: [UUID] = []

    func periods(observerId: UUID, during day: DateInterval, backend: URL) async throws -> [Period] {
        requested.append(observerId)
        return [Period(id: UUID(), name: "Safari", identifiers: [], startAt: day.start + 3_600, endAt: day.start + 4_000)]
    }
}
