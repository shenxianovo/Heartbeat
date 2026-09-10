# 01 — Browser 临时 FOI 运行逻辑

Status: done

问题：Browser 窗口能否只作为 Collector 运行状态，支持独立的并行活动和事实输出？

## 范围与验收

- [x] 使用基线 `3d512d0` 的实际 `src/fold.ts`；无生产逻辑修改。
- [x] 双窗口演练：关闭 17 后只剩窗口 23；活动 1 已结束，活动 2 沿用身份继续增长。
- [x] 页面切换演练：窗口 17 的 A → B → A 产生三项不同活动；关闭后运行窗口数为零。
- [x] 关闭再开演练：原窗口 17 的活动 1 保留，新窗口 31 使用活动 2。
- [x] 单文件原型、纯逻辑输出与源码保存在独立分支，生成脚本语法检查通过。

## 证据与复用

- 分支：`codex/observation-runtime-prototype`。
- 提交：`c110063b286b938d9defc124da3bac0e67a5936b`。
- 分支内目录：`collection/collectors/Heartbeat.Collector.Browser/prototypes/observation-runtime/`。
- 直接打开 `prototype.html`；`walkthrough-output.json` 保存三个场景每步实际 fold 状态与快照。
- `build.mjs` 将现有 fold 移除类型并内联到 HTML，未依赖 Web 服务或持久化；页面固定 URL 与可读 ID 是演示依赖。

结论：`FoldState.open[windowId]` 已足以承担临时 FOI 的运行职责；关闭窗口只移除运行状态，
终态快照已输出。现有逻辑已具备所验证的行为，无需将原型业务逻辑再合入主分支。
没有新增 Objects、关系表、观测 ContextId 或 SDK 状态机；存储与输出契约保持现状。

内置浏览器 URL 策略拒绝打开本地 HTML，因此没有页面交互/视觉验收。
记录的是纯逻辑演练与生成脚本语法检查，不是生产持久化、真实窗口事件或端到端验收。
