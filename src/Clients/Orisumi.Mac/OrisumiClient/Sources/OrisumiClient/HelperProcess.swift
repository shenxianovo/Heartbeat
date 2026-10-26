import Foundation

/// How to start the helper.
public struct HelperConfiguration: Equatable, Sendable {
    public var executable: URL
    public var backend: URL
    /// Replaces the helper's default identity and registry files, for development against another environment.
    public var dataDirectory: URL?

    public init(executable: URL, backend: URL, dataDirectory: URL? = nil) {
        self.executable = executable
        self.backend = backend
        self.dataDirectory = dataDirectory
    }

    public var arguments: [String] {
        var arguments = ["--backend", backend.absoluteString]
        if let dataDirectory {
            arguments += [
                "--identity-file", dataDirectory.appending(path: "identity.json").path,
                "--applications-file", dataDirectory.appending(path: "local-applications.json").path,
            ]
        }
        return arguments
    }

    /// An HTTP address with a host, or nil.
    public static func backend(from text: String) -> URL? {
        guard let url = URL(string: text.trimmingCharacters(in: .whitespaces)),
              url.scheme == "http" || url.scheme == "https",
              url.host() != nil else { return nil }
        return url
    }
}

/// Runs one helper process and reports its events and exit on the main actor.
@MainActor
public final class HelperProcess {
    public enum Exit: Equatable, Sendable {
        case requested
        case unexpected(status: Int32, message: String?)
    }

    public var onEvent: (HelperEvent) -> Void = { _ in }
    public var onExit: (Exit) -> Void = { _ in }
    public var isRunning: Bool { process != nil }

    private var process: Process?
    private var stopRequested = false
    private var remainingParts = 0
    private var status: Int32 = 0
    private var lastError: String?
    private var waiters: [CheckedContinuation<Void, Never>] = []

    public init() {}

    public func start(_ configuration: HelperConfiguration) throws {
        guard process == nil else { return }
        let process = Process()
        process.executableURL = configuration.executable
        process.arguments = configuration.arguments
        let output = Pipe(), errors = Pipe()
        process.standardOutput = output
        process.standardError = errors
        // The exit is reported after both pipes close and the process terminates, so no line arrives after it.
        remainingParts = 3
        lastError = nil
        read(output.fileHandleForReading) { [weak self] line in
            if let event = HelperEvent.parse(line) { self?.onEvent(event) }
        }
        read(errors.fileHandleForReading) { [weak self] line in
            if !line.isEmpty { self?.lastError = line }
        }
        process.terminationHandler = { [weak self] process in
            let status = process.terminationStatus
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self?.status = status
                    self?.finishPart()
                }
            }
        }
        do { try process.run() } catch {
            output.fileHandleForReading.readabilityHandler = nil
            errors.fileHandleForReading.readabilityHandler = nil
            throw error
        }
        self.process = process
    }

    /// Sends SIGINT so the helper ends the active period at its last confirmation, then waits for it to exit.
    public func stop() async {
        guard let process else { return }
        stopRequested = true
        if process.isRunning { process.interrupt() }
        let escalation = Task { [weak self] in
            try? await Task.sleep(for: .seconds(10))
            if self?.process === process, process.isRunning { process.terminate() }
        }
        await withCheckedContinuation { waiters.append($0) }
        escalation.cancel()
    }

    private func read(_ handle: FileHandle, line: @escaping @MainActor (String) -> Void) {
        let reader = LineReader { text in
            DispatchQueue.main.async { MainActor.assumeIsolated { line(text) } }
        } finished: { [weak self] in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.finishPart() } }
        }
        handle.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil
                reader.finish()
            } else {
                reader.append(data)
            }
        }
    }

    private func finishPart() {
        remainingParts -= 1
        guard remainingParts == 0 else { return }
        process = nil
        let exit: Exit = stopRequested ? .requested : .unexpected(status: status, message: lastError)
        stopRequested = false
        let waiters = self.waiters
        self.waiters = []
        waiters.forEach { $0.resume() }
        onExit(exit)
    }
}

/// Splits pipe data into lines. FileHandle calls its readability handler serially, so no lock is needed.
private final class LineReader: @unchecked Sendable {
    private var buffer = Data()
    private let line: @Sendable (String) -> Void
    private let finished: @Sendable () -> Void

    init(line: @escaping @Sendable (String) -> Void, finished: @escaping @Sendable () -> Void) {
        self.line = line
        self.finished = finished
    }

    func append(_ data: Data) {
        buffer.append(data)
        while let newline = buffer.firstIndex(of: UInt8(ascii: "\n")) {
            emit(buffer[buffer.startIndex..<newline])
            buffer.removeSubrange(buffer.startIndex...newline)
        }
    }

    func finish() {
        if !buffer.isEmpty { emit(buffer) }
        buffer.removeAll()
        finished()
    }

    private func emit(_ data: Data) {
        line(String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines))
    }
}
