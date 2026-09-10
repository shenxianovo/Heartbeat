# 02 — 开发扩展自动 Reload

Status: done

用户要求启动/构建后自动 Reload 开发扩展，沿用原 Profile 绑定和状态保持更新。

## 验收

- [x] 仅 development 构建启用；启动及独立 MV3 闹钟检查本地构建变化，采集停用仍可检查。
- [x] 只接受已固定的同一 Desktop Profile；保留原连接校验，旧代码不能使用新精确包身份连接。
- [x] 串行事件队列内先持久化当前活动，再调用 runtime.reload；持久化失败不 Reload。
- [x] 每个新构建最多自动尝试一次，防止坏构建循环；扩展身份、配置与 outbox 不删除。
- [x] 首次 Load unpacked / 旧扩展手动 Reload 一次后，后续构建无需重复点击；文档说明例外。
- [x] 自动回归及真实 Chrome 不注入 reload 调用的换包/重连/状态保留验证通过。

## Comments

2026-09-10：新增回归先失败后通过；Browser 全部 105 项测试通过。
采用 Chrome 官方 runtime.reload 和 30 秒 MV3 alarms，无远程调试端口或生产 Host 控制面。


2026-09-10 closeout：真实 Chrome 更新精确包后约 26 秒自行 Reload，测试未注入 runtime.reload 调用；
扩展身份、本地状态保持，新包重连成功，生产形态 handler 无连接。
证据与可重复脚本：`.local/browser-verification/chrome-auto-reload-report.json`、`chrome-auto-reload-smoke.mjs`。
两轴复查发现普通 enqueue 可降级成 Gap，现已改为严格 checkpoint，完整写入后才 Reload；
新增 checkpoint 回归验证写入失败和容量不足不会将 Gap 当成活动持久化成功，修复后全部测试通过。
用户随后要求不再扩展边界场景，本轮按已实现能力收口。Windows/Edge 整体验收仍归 issue 01。
