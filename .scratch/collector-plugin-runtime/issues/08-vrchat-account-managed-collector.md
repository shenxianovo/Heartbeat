# 08 — VRChat Account Collector 接入无头 Hub

**What to build:** 把现有 VRChat 终端原型改成由无头 Hub 管理的 ManagedProcess Collector，以 VRChat Account 为 Subject 持续发布 presence Segment，并通过统一 Runtime 暴露运行状态和恢复行为。

**Blocked by:** 06 — 无头 Hub 运行 ManagedProcess 参考 Collector.

**Status:** ready-for-human

- [x] VRChat Collector 作为自包含 Package 启动，只依赖 Collector Protocol，不直接依赖 Hub 内部服务或 Heartbeat Server 地址。
- [x] 每个配置的 VRChat 账号对应稳定 Collector Instance 和 Account Subject；同一个无头 Hub 可以同时托管多个账号及其他主题的 Instance。
- [x] 世界/实例在线状态折叠为稳定 FactId、递增 Revision 的 presence Segment，并在切换或停止时发送最终快照。
- [x] Hub 不可达、ACK 丢失和进程重启时 outbox 保留并幂等重发；无法恢复的丢失通过 Stream Gap 披露。
- [x] 凭据与 cookie 不进入 Manifest、Fact、普通日志或 Runtime State；现有认证行为可通过 mock VRChat API 做离线集成测试。
- [x] 实际外部账号验证只作为人工 smoke test，自动测试不依赖真实 VRChat 服务或用户凭据。

## Comments

- 2026-08-27：实现完成，等待本地/真实账号人工 smoke。Headless Hub 通过 owner-only OIDC 管理 API 向 Dashboard 暴露非阻塞登录 challenge；VRChat 会话进入按 Collector Instance 隔离的加密 secret store。Presence 保留原始 instance 字符串，待本地 E2E 后再决定结构化语义。
- 自动验证：`dotnet test Heartbeat.slnx --no-restore`、`npm test`、`npm run build`，以及 Headless Docker image build。
