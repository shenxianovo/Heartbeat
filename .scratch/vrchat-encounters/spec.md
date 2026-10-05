# VRChat 服务器事件与世界可视化

Status: ready-for-human

2026-09-27 用户确认“先做在服务器 Hub 能做的”。本轮实现服务器 Pipeline 事件 + 快照核对、自己位置与 API 可见同场记录，以及 Web 世界气泡/相遇/热力图。

决策和协议已移入 docs/adr/ADR-0026-vrchat-server-event-observation.md 与 Collector README。真实事件必须由用户手动 E2E，执行步骤在 docs/validation/vrchat-server-events.md；不新增无意义的模拟事件单元测试。本轮不部署。

人工验收未完成前保留本记录。PC/Quest 日志接入与服务器记录的去重、补全、权威选择另见 .scratch/vrchat-source-reconciliation/issues/01-source-reconciliation.md，本轮不实现。

## 自动验证证据

2026-09-27，以 `ce0a1e79a8832311366a94f8ac1ad9a7ebdf52ac` 为比较基点：

- `dotnet run --project tools/Heartbeat.Dev -- verify closeout --base ce0a1e79a8832311366a94f8ac1ad9a7ebdf52ac`：构建/现有回归、前端检查、浏览器检查和结构质量闸门通过。证据：`.artifacts/verification/20260927T050131Z-verify-closeout-f6d7994d1e124b81af3b477636f15c48/`。
- `dotnet run --project tools/Heartbeat.Dev -- scenario replay-fixture`：通过，已查看桌面及窄屏 VRChat 截图。证据：`.artifacts/verification/20260927T050318Z-scenario-replay-fixture-885830a27676428abb981e96df2efeba/`。

浏览器认证和记录响应为 fixture，只证明页面展示与交互。真实 VRChat 登录、事件覆盖、429 和 PCVR/Quest 人工 E2E 尚未执行；未部署。
