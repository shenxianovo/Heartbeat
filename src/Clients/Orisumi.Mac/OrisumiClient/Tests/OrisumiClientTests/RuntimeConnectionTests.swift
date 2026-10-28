import Foundation
import Testing
@testable import OrisumiClient

/// Runs shell scripts in place of Runtime.
@MainActor
@Suite(.serialized)
struct RuntimeConnectionTests {
    static func connection(_ script: String, grace: Duration = .seconds(20)) throws -> (RuntimeConnection, URL) {
        let directory = FileManager.default.temporaryDirectory.appending(path: "orisumi-runtime-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let executable = directory.appending(path: "runtime.sh")
        try ("#!/bin/sh\n" + script).write(to: executable, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        let configuration = RuntimeConfiguration(executable: executable, dataDirectory: directory.appending(path: "data"),
                                                 backend: URL(string: "http://orisumi.test/api/")!,
                                                 bundled: directory.appending(path: "bundled"))
        return (RuntimeConnection(grace: grace) { configuration }, directory)
    }

    /// Answers each request with its own id; `shutdown` makes it exit.
    static let echo = """
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -E 's/.*"id":"([^"]*)".*/\\1/')
          case "$line" in
            *'"method":"shutdown"'*) printf '{"id":"%s","result":{}}\\n' "$id"; exit 0 ;;
            *'"method":"fail"'*) printf '{"id":"%s","error":{"code":"not_found","message":"missing"}}\\n' "$id" ;;
            *) printf '{"event":"ignored"}\\n{"id":"%s","result":{"args":"%s"}}\\n' "$id" "$*" ;;
          esac
        done
        """

    @Test func matchesResponsesAndReportsErrors() async throws {
        let (connection, directory) = try Self.connection(Self.echo)
        var exits: [RuntimeExit] = []
        connection.onExit = { exits.append($0) }
        try connection.start()
        #expect(FileManager.default.fileExists(atPath: directory.appending(path: "data").path))

        let result = try await connection.request("list", Data("{}".utf8))
        let text = String(decoding: result, as: UTF8.self)
        #expect(text.contains("--data-dir"))
        #expect(text.contains("--bundled"))
        await #expect(throws: RuntimeError.remote(code: "not_found", message: "missing")) {
            try await connection.request("fail", Data("{}".utf8))
        }
        // Requests wait for each other, so concurrent callers get their own answers.
        async let first = connection.request("a", Data("{}".utf8))
        async let second = connection.request("b", Data("{}".utf8))
        _ = try await (first, second)

        await connection.shutdown()
        #expect(exits == [.requested])
        #expect(!connection.isRunning)
    }

    @Test func reportsAnUnexpectedExitAndFailsThePendingRequest() async throws {
        let (connection, _) = try Self.connection("read -r line; echo 'disk full' >&2; exit 3")
        var exits: [RuntimeExit] = []
        connection.onExit = { exits.append($0) }
        try connection.start()
        await #expect(throws: RuntimeError.notRunning) {
            try await connection.request("list", Data("{}".utf8))
        }
        try await waitUntil { !exits.isEmpty }
        #expect(exits == [.unexpected(status: 3, message: "disk full")])
    }

    @Test func endsRuntimeThatIgnoresShutdownByClosingItsInput() async throws {
        // Ignores requests and exits when standard input closes.
        let (connection, _) = try Self.connection("cat > /dev/null", grace: .milliseconds(200))
        try connection.start()
        let started = Date.now
        await connection.shutdown()
        #expect(!connection.isRunning)
        #expect(Date.now.timeIntervalSince(started) < 5)
    }
}
