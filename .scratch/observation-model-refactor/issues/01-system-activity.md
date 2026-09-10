# System 桌面活动观测模型

Status: done

## 验收

- [x] SystemActivityModel 只判定活动转场，可用桌面观察值独立驱动，无 Fact/协议/持久对象登记。
- [x] AppMonitorService 使用模型结果生成现有 ForegroundSegmentSnapshot；前台/away 共用当前 Segment 状态。
- [x] 标题门控、明确的 App/窗口转场、away/恢复、快照连续性、超长段轮转、停止及延迟交付场景保持通过。
- [x] System 测试、解决方案构建和命名检查通过，记录证据。
- [x] 文档说明职责与实际验证范围。

## 设计

一个模型实例对应当前采集路径绑定的本机桌面，不额外生成对象 ID。Current 表示接受的活动读数，
未通过点击门控的标题仅用于识别下一次标题变化，保持当前 Facts 的活动标题语义。
传输与持久化仍由现有代码负责；这不是通用 SDK 的首次实现。

## 验证

改造前：解决方案构建 0 warning / 0 error；IDE1006 检查通过；System 89 项测试通过。

改造后（2026-09-10）：

- `dotnet build Heartbeat.slnx --no-restore`：通过，0 warning / 0 error。
- `dotnet test collection/desktop/Heartbeat.Collector.System.Tests --no-build`：92 passed，0 failed，0 skipped。
  保留原 89 项测试，新增 3 项直接运行模型的场景；包括现有协议 transcript、跨进程 ingress 及崩溃重放回归。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- `git diff --check`：通过。
- 生产 C#：新增 144 行（含新文件），删除 210 行，净减少 66 行；新增测试 54 行，总 C# 净减少 12 行。

本项初次验收覆盖代码和自动验证。当时未安装、发布或操作日常运行的 Desktop；后续本机运行证据见下方。
可在后续使用本次构建时核对应用/窗口切换、30 秒快照及 away/恢复。

## Comments

2026-09-10：用户选定“观测模型改造”，先 System，便于后续真机核对。


### 2026-09-10 本机运行检查

使用当前 Debug 构建启动 macOS Desktop，独立 Profile 为 `.local/observation-system`，
PID 64110，`desktop-status.json` 确认 `uiReady=true`，安装自启动与更新能力均关闭。
日常安装版保持运行，未更换安装产物。开发实例继续运行供用户观察。

真实 macOS App 回调已生成 6 条本地 Segment 快照记录（检查时点）：
同一活动 FactId 在两次检查间 Revision 从 1 增至 3，Start 不变，
End 从 01:06:36 UTC 延长到 01:07:36 UTC，仍为非终态。
证据：`.local/observation-system/observation-verification.json`，只记录身份/时间及就绪状态。

本次确认原生宿主启动、App 转场及定时快照进入本地 Runtime；未配置 API key，未验证服务端上传。
标题/点击辅助/输入事件未启用，away/恢复和这些可选能力仍未做真机操作验收。
日志没有 ERR/FTL；告警为未配置 API key、无 token 和本地状态上传网络失败。

2026-09-10：用户查看运行结果后反馈“我看没问题”，同意提交本次改造。
此反馈记录为当前开发实例的使用确认，不额外推断标题、away 或服务端上传已验收。
