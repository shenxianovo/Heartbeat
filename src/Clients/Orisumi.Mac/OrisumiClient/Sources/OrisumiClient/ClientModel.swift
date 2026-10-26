import Foundation
import Observation

/// What the menu and window show: whether collection runs, today's periods and the delivery status.
@MainActor
@Observable
public final class ClientModel {
    public enum State: Equatable, Sendable {
        case running
        case paused
        case failed(String)
    }

    public private(set) var state: State = .paused
    public private(set) var checkpoint: Checkpoint?
    /// Periods that ended since the client started, newest first. Each ends at its last checkpoint.
    public private(set) var periods: [Checkpoint] = []
    public private(set) var delivery: Delivery?
    /// The program instance whose periods are shown, learned from the helper's checkpoints.
    public private(set) var observerId: UUID?
    /// Today's periods read from the API at the last refresh.
    public private(set) var saved: [Period] = []
    public private(set) var readError: String?

    private let helper: HelperProcess
    private let source: ActivitySource
    private let configuration: () -> HelperConfiguration

    public init(helper: HelperProcess = HelperProcess(), source: ActivitySource = ActivityReader(),
                configuration: @escaping () -> HelperConfiguration) {
        self.helper = helper
        self.source = source
        self.configuration = configuration
        helper.onEvent = { [weak self] event in self?.receive(event) }
        helper.onExit = { [weak self] exit in self?.exited(exit) }
    }

    public func start() {
        guard !helper.isRunning else { return }
        do {
            try helper.start(configuration())
            state = .running
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    public func pause() async {
        await helper.stop()
        endCheckpoint()
        state = .paused
    }

    /// Applies new settings by starting the helper again.
    public func restart() async {
        await helper.stop()
        endCheckpoint()
        start()
    }

    public func shutdown() async {
        await helper.stop()
    }

    /// Today's periods: saved ones, those still local, and the active one. Newest first.
    public func today(now: Date = .now) -> [Period] {
        Today.merge(saved: saved, local: periods.map { Period($0, isActive: false) },
                    active: checkpoint.map { Period($0, isActive: true) },
                    day: Today.interval(containing: now), now: now)
    }

    public func refreshToday(now: Date = .now) async {
        guard let observerId else { return }
        do {
            saved = try await source.periods(observerId: observerId, during: Today.interval(containing: now),
                                             backend: configuration().backend)
            readError = nil
        } catch {
            readError = "无法读取 API，只显示本机还没发送的时段。"
        }
    }

    private func receive(_ event: HelperEvent) {
        switch event {
        case .checkpoint(let checkpoint):
            if self.checkpoint?.observationId != checkpoint.observationId { endCheckpoint() }
            self.checkpoint = checkpoint
            if let observerId = checkpoint.observerId, observerId != self.observerId {
                self.observerId = observerId
                Task { await refreshToday() }
            }
        case .delivery(let delivery):
            // Newly saved periods move from local to the API.
            let saved = delivery.saved != self.delivery?.saved
            self.delivery = delivery
            if saved { Task { await refreshToday() } }
        }
    }

    private func exited(_ exit: HelperProcess.Exit) {
        guard case .unexpected(let status, let message) = exit else { return }
        endCheckpoint()
        state = .failed(message ?? "helper 已退出，状态码 \(status)")
    }

    private func endCheckpoint() {
        guard let checkpoint else { return }
        periods.insert(checkpoint, at: 0)
        if periods.count > 100 { periods.removeLast() }
        self.checkpoint = nil
    }
}
