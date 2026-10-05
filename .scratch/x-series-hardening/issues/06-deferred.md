# 06 本轮不做的两条

Status: `needs-triage`
覆盖候选: `X-A3` + `W-10`、`X-E1`

## X-A3 / W-10 前端组合层可测性 — 顺延

`ReplayWorkbench.tsx`(306)、`DensityCurve.tsx`(256)、`TimelineLane.tsx`(221)、`ActivityOverview.tsx`(191)、`api/client.ts`、`api/queries.ts` 都没有单测。唯一的证明是跑在 fixture 上的浏览器测试，断言又大量落在 CSS class 和几何数值上——它证明「渲染出了预期形状」，不证明「编排逻辑正确」。

要真正修掉，前提是先把 `ReplayWorkbench` 的范围状态、勾选状态、查询编排抽成可测 seam（也就是 `W-10`）。那是一次独立的前端重构，会同时动到 `W-02`（tile 缓存失效）、`W-04`（Track 失败隔离）、`W-09`（渲染期读缓存）——这几条本来就该一起改。

**建议**：下一轮单独立一个 feature，把 `W-02`/`W-04`/`W-09`/`W-10`/`X-A3` 打包做，顺带把 `W-01`（跨日时间标签）带上。

## X-E1 生产代码重复 — 本轮不动

jscpd 量到 7 处生产重复，最典型的是两个 macOS native 观察器约 29 行几乎相同的桥接代码（`MacAccessibilityNative.cs:99-110↔MacInputMonitoringNative.cs:73-84` 等）。

不在本轮动的理由：
1. 涉及 P/Invoke 与 Cocoa 回调边界，抽取时最容易出问题的不是逻辑而是异常类型与生命周期（这类坑本仓踩过）。
2. 本机 `env up desktop` 有 `dotnet watch` 在跑 Mac Collector，改这两个文件会触发热重载，验证结果不干净。
3. 它是 P3，且 Application/Infrastructure 那几对重复（`CountPointRecords` ↔ `ReplayRecords`）更适合和 `S-12`（Endpoints 职责越界）一起做。

**建议**：等 S 系列那一轮一起收。

## Comments

### 2026-09-17（来自 issue 04）

**真端到端场景 — 顺延。** 本轮把 `scenario replay-fixture` / `scenario delivery` 的说法降级成它们实际做的事（复跑第一层测试的一个子集并留证据），没有让它们变成真链路。现在只有 `native-desktop` 会起进程、起数据库、需要人真的操作。

要补上「真端到端」，需要的是把 Web + Hub + Collector 作为一条链路跑起来并留证据（独立 Compose 项目、真实认证、真实上传与回放），那是一次独立工作，不该顺手塞进 Developer CLI 里，否则 CLI 会继续变大而每一层都更模糊。

**建议**：单独立一条，和 issue 01（真实认证链路覆盖）一起考虑——两者需要的运行环境是同一套。
