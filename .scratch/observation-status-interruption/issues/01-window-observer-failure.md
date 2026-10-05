Status: needs-info

# 窗口标题观测状态短暂不可用后恢复

用户于 2026-10-01 确认在时间线的观测状态轨道看到短暂不可用。本轮即时投递已获授权，观测语义调整尚未确定。

## 证据

2026-10-01 对本地 `heartbeat-db-1` 只读查询近七天的 `desktop.observation.status`，只提取 capability/state/reason 和时间统计，不读取窗口标题、输入或凭据。SQL 与结果保存在 `.artifacts/verification/20261001-observation-status-diagnosis/query.sql`、`status-summary.txt`。不可用记录均为 `window_title / unavailable / observer_failed`；没有权限不足记录，也没有应用或输入能力的不可用记录。数据库记录只证明 Collector 当时报告了窗口观察失败，不证明具体 AX 调用失败原因。

代码入口为 `MacSystemObservationSource.OnAccessibilityFailure`，读取异常与异步监听器异常共用这个原因码；周期 `RefreshCapabilities` 会尝试恢复观察。五秒确认还读取前台应用和窗口，延长已确认区间，不只是权限检查。当前保留它，不能通过去掉确认来掩盖观测失败。

## 下一步

已搜索仓库 `.local`、`.artifacts` 中的日志，没有找到这批 `macOS window-title observation failed` 原生错误，现有历史记录无法区分具体 AX 错误码。下一次自然出现时需要对应时间的原生异常（只取异常类型和 AX 错误码，不保存标题），再建立最小失败回归。不能把本轮投递测试通过声明为这个现象已修复。
