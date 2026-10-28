import Foundation

/// How to start Runtime.
public struct RuntimeConfiguration: Equatable, Sendable {
    public var executable: URL
    public var dataDirectory: URL
    public var backend: URL
    /// Packages that ship with the client.
    public var bundled: URL
    /// The official catalog. Without it only bundled packages are offered.
    public var catalog: URL?

    public init(executable: URL, dataDirectory: URL, backend: URL, bundled: URL, catalog: URL? = nil) {
        self.executable = executable
        self.dataDirectory = dataDirectory
        self.backend = backend
        self.bundled = bundled
        self.catalog = catalog
    }

    public var arguments: [String] {
        var arguments = [
            "--data-dir", dataDirectory.path,
            "--backend", backend.absoluteString,
            "--bundled", bundled.path,
        ]
        if let catalog { arguments += ["--catalog", catalog.absoluteString] }
        return arguments
    }

    /// An HTTP address with a host, or nil.
    public static func address(from text: String) -> URL? {
        guard let url = URL(string: text.trimmingCharacters(in: .whitespaces)),
              url.scheme == "http" || url.scheme == "https",
              url.host() != nil else { return nil }
        return url
    }
}

public enum RuntimeExit: Equatable, Sendable {
    case requested
    case unexpected(status: Int32, message: String?)
}

public enum RuntimeError: Error, Equatable, LocalizedError {
    /// Runtime answered with an error object.
    case remote(code: String, message: String)
    case notRunning
    case invalidResponse

    public var errorDescription: String? {
        switch self {
        case .remote(_, let message): message
        case .notRunning: "Runtime 没有运行"
        case .invalidResponse: "Runtime 的响应无法解析"
        }
    }

    public var code: String? {
        if case .remote(let code, _) = self { code } else { nil }
    }
}

/// What the model needs from Runtime. Tests replace it.
@MainActor
public protocol RuntimeTransport: AnyObject {
    var onExit: (RuntimeExit) -> Void { get set }
    var isRunning: Bool { get }
    func start() throws
    /// Sends one request and returns its `result` as JSON.
    func request(_ method: String, _ parameters: Data) async throws -> Data
    /// Asks Runtime to stop its Observers and exit, and waits until it has.
    func shutdown() async
}

/// Runs the Runtime process and exchanges JSON lines with it over standard input and output.
/// Requests go one at a time; responses are matched by `id`.
@MainActor
public final class RuntimeConnection: RuntimeTransport {
    public var onExit: (RuntimeExit) -> Void = { _ in }
    public var isRunning: Bool { process != nil }

    private let configuration: () -> RuntimeConfiguration
    /// How long `shutdown` waits before closing standard input, and again before terminating.
    private let grace: Duration
    private var process: Process?
    private var input: FileHandle?
    private var stopRequested = false
    private var remainingParts = 0
    private var status: Int32 = 0
    private var lastError: String?
    private var nextId = 0
    private var pending: [String: CheckedContinuation<Data, any Error>] = [:]
    private var busy = false
    private var queue: [CheckedContinuation<Void, Never>] = []
    private var exitWaiters: [CheckedContinuation<Void, Never>] = []

    public init(grace: Duration = .seconds(20), configuration: @escaping () -> RuntimeConfiguration) {
        self.grace = grace
        self.configuration = configuration
    }

    public func start() throws {
        guard process == nil else { return }
        // A write after Runtime exits must fail with EPIPE instead of killing the client.
        signal(SIGPIPE, SIG_IGN)
        let configuration = configuration()
        try FileManager.default.createDirectory(at: configuration.dataDirectory, withIntermediateDirectories: true)
        let process = Process()
        process.executableURL = configuration.executable
        process.arguments = configuration.arguments
        let input = Pipe(), output = Pipe(), errors = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        // The exit is reported after both pipes close and the process terminates, so no line arrives after it.
        remainingParts = 3
        lastError = nil
        stopRequested = false
        read(output.fileHandleForReading) { [weak self] line in self?.receive(line) }
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
        self.input = input.fileHandleForWriting
    }

    public func request(_ method: String, _ parameters: Data) async throws -> Data {
        await acquire()
        defer { release() }
        guard let input, process != nil else { throw RuntimeError.notRunning }
        nextId += 1
        let id = String(nextId)
        var line = Data(#"{"id":"\#(id)","method":"\#(method)","params":"#.utf8)
        line.append(parameters)
        line.append(Data("}\n".utf8))
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = continuation
            do { try input.write(contentsOf: line) } catch {
                pending[id] = nil
                continuation.resume(throwing: RuntimeError.notRunning)
            }
        }
    }

    public func shutdown() async {
        guard let process else { return }
        stopRequested = true
        if process.isRunning {
            // Runtime exits after `shutdown`; closing standard input asks the same if the request cannot be sent.
            Task { _ = try? await request("shutdown", Data("{}".utf8)) }
            let escalation = Task { [weak self, grace] in
                try? await Task.sleep(for: grace)
                guard self?.process === process else { return }
                try? self?.input?.close()
                try? await Task.sleep(for: grace)
                if self?.process === process, process.isRunning { process.terminate() }
            }
            await withCheckedContinuation { exitWaiters.append($0) }
            escalation.cancel()
        } else {
            await withCheckedContinuation { exitWaiters.append($0) }
        }
    }

    private func receive(_ line: String) {
        guard !line.isEmpty,
              let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
              let id = object["id"] as? String,
              let continuation = pending.removeValue(forKey: id) else { return }
        if let error = object["error"] as? [String: Any] {
            continuation.resume(throwing: RuntimeError.remote(code: error["code"] as? String ?? "internal",
                                                              message: error["message"] as? String ?? ""))
        } else if let result = object["result"],
                  let data = try? JSONSerialization.data(withJSONObject: result, options: [.fragmentsAllowed]) {
            continuation.resume(returning: data)
        } else {
            continuation.resume(throwing: RuntimeError.invalidResponse)
        }
    }

    private func acquire() async {
        if busy { await withCheckedContinuation { queue.append($0) } } else { busy = true }
    }

    private func release() {
        if queue.isEmpty { busy = false } else { queue.removeFirst().resume() }
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
        try? input?.close()
        input = nil
        let failed = pending
        pending = [:]
        failed.values.forEach { $0.resume(throwing: RuntimeError.notRunning) }
        let exit: RuntimeExit = stopRequested ? .requested : .unexpected(status: status, message: lastError)
        stopRequested = false
        let waiters = exitWaiters
        exitWaiters = []
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
