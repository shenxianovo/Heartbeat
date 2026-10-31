import OrisumiClient
import UserNotifications

/// Sends a system notification when an Observer has more rejected writes than at the last look.
@MainActor
final class RejectionNotifier {
    private var seen: [UUID: Int]?

    func update(_ observers: [ObserverStatus]) {
        let current = Dictionary(uniqueKeysWithValues: observers.map { ($0.instanceId, $0.writes.rejected) })
        defer { seen = current }
        guard let seen else {
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { _, _ in }
            return
        }
        for observer in observers where observer.writes.rejected > seen[observer.instanceId, default: 0] {
            let content = UNMutableNotificationContent()
            content.title = "\(observer.name) 有写入被拒绝"
            content.body = observer.writes.lastRejection ?? "在这个 Observer 的详情中查看。"
            UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }
}
