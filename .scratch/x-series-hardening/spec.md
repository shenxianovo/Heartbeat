# Spec: X 系列加固（工程与架构腐化的收口）

Base: `27cdf6f`（`cleanroom-rewrite`）
起因: `.scratch/implementation-review/candidates.md` 的 X 分区（横切：测试证明力、文档一致性、Developer CLI、仓库规范）。用户判断——功能实现 main 上都有可参考，重写的理由是架构与工程腐化，所以这一轮只修 X，C 系列缓办。

## 这一轮要达到的状态

1. **闸门能度量存量，不只拦增量。** 跨基点度量必须真的跑得通；跑不通要给出能行动的诊断，而不是一堆 0。
2. **测试证明的是真实行为。** 认证这条最要紧的链路不能只由测试替身背书。
3. **文档说的和代码做的一致。** 所有写死的数字、宣称已实测但没有证据的句子，要么改对、要么降级。
4. **闸门的口径与文档一致。** 参与闸门的检查就说参与，观察项就别悄悄挂人。
5. **决策有记录，卫生有归位。** 架构级决定补 ADR；`.scratch` 做完就删；main 上的病历带过来变成收口检查项。

## 不在这一轮

- `X-A3` / `W-10`：前端组合层可测性，要先把 `ReplayWorkbench` 的状态与查询编排抽成 seam，是独立一轮的体量。见 `issues/06-deferred.md`。
- `X-E1`：生产代码重复（macOS native 桥接），涉及 P/Invoke，且本机有 `dotnet watch` 在跑 Collector，本轮不动。见 `issues/06-deferred.md`。
- C / S / W 系列：按用户判断顺延。`S-02`（audience 默认不校验）本轮只做「钉住当前行为 + 留决策」，不改验收行为。

## 不变量

- 遵守 `AGENTS.md`：不加兼容分支、不加协议版本、一份当前实现连测试和文档一起改。
- 遵守 `AGENTS.md:14`：会改变对外验收行为的设计决策，先摆出来等用户拍，不在这一轮偷偷落地。
- 本机有 `env up desktop`（`dotnet watch` 在跑 Mac Collector）。构建与测试尽量按项目定向跑，避免和 watch 抢 `bin/obj`。

## Issues

| # | 标题 | 覆盖候选 |
|---|---|---|
| 01 | 真实认证管线端到端覆盖 | `X-A2`、`S-04`，钉住 `S-02`/`S-03` 当前行为 |
| 02 | 结构闸门要度量存量 | `X-A1`、`X-C3`、`X-C5`、`X-C2` |
| 03 | 文档与代码对账 | `X-B1`、`X-B2`、`X-B4`、`X-B5`、`X-A6`、`X-E2` |
| 04 | 闸门口径诚实化与工具卫生 | `X-B3`、`X-A5`、`X-C4`、`X-D4`、`X-A4` |
| 05 | 决策记录与仓库卫生 | `X-D1`、`X-D2`、`X-D3`、`X-C1`、`M-EX`、`M-03b` |
| 06 | 本轮不做的两条（记录理由） | `X-A3`/`W-10`、`X-E1` |

## 本轮验证结果

本轮结束时的收口验证（2026-09-17，工作树未提交，HEAD 仍是 `27cdf6f`；本机 `env up desktop` 的 `dotnet watch` 全程未动）。

### 1. 全解决方案测试 `dotnet test Heartbeat.slnx -c Debug`

退出码 **0**，无 bin/obj 争用错误（一次跑过，未重试）：

| 测试程序集 | Passed | Failed | Skipped |
|---|---|---|---|
| Heartbeat.Domain.Tests | 27 | 0 | 0 |
| Heartbeat.Collector.Desktop.Mac.Tests | 98 | 0 | 0 |
| Heartbeat.Hub.Tests | 54 | 0 | 0 |
| Heartbeat.Dev.Tests | 142 | 0 | 0 |
| Heartbeat.Integration.Tests | 107 | 0 | 0 |
| **合计** | **428** | **0** | **0** |

### 2. `dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD`

退出码 **0**，因改动面覆盖较广，选检结果是 `full-fallback` 计划，三阶段全绿：

- `dotnet`：`dotnet test Heartbeat.slnx --no-restore --verbosity minimal` → 428 passed / 0 failed（同上分布）。
- `web`：`npm run verify`（lint + 类型检查 + vitest + `next build`）→ 通过，静态页 `/`、`/_not-found`、`/auth/callback`、`/login` 全部预渲染成功。
- `browser`：`npm run test:e2e`（Playwright chromium，9 workers）→ **17 passed (7.4s)**，含 replay 密度曲线、悬停浮层、泳道缩放/键盘、日期选择器、失败重试等。截图 5 张落在证据目录 `playwright/`。

证据目录：`.artifacts/verification/20260917T093848Z-verify-full-fallback-7b29275a7ff74c5dbf0bebad7d47254d`
（`manifest.json` 的 `exitCode: 0`、`failure: null`；已声明的限制仍是「浏览器侧的认证与 API 响应是 mock 的，通过不等于部署链路被证明」。）

### 3. `dotnet run --project tools/Heartbeat.Dev -- quality --base anchor --stock`（存量，累计口径，不闸门）

退出码 **0**（存量测量本身不判定成败）。对 anchor `4e15d57` 的数字：

- LOC：生产 **11,564**（+10,955）；测试 **8,781**；工具 **4,362**；build 443（不计生产）。生产按语言：C# 6,181 / CSS 1,747 / TypeScript 3,636。
- 重复：生产 **14 簇**（1.05% 重复行）；测试 **15 簇**（1.62%）。相对 anchor 全部算「新增」，因为 anchor 几乎是空仓。生产侧的簇集中在 `RecordEndpoints`↔`TrackEndpoints`、`CountPointRecords`↔`ReplayRecords`、两个 Postgres 读存储、两个 Mac Native 互操作封装。
- 复杂度：**12 个生产热点** > 10，存量预算 12（贴顶，未超）；最高几处为 `MacSystemObservationSource.RefreshCapabilities`(17)、`OnWorkspaceNotification`(15)、`ReplayRecords.ExecuteAsync`(14)、`DesktopCollectorSession.RunAsync`(13)。
- erosion：**40.11%**（12/910 高风险函数）。
- C# coupling：2 个热点（CA1506，仅观测）。

证据目录：`.artifacts/verification/20260917T093946Z-quality-stock-0ccb2afec05542b19c1d9721abda8f79`

### 仍然「红」的东西

按增量闸门口径（`--base HEAD`）**没有红项**：`quality --base HEAD` 退出码 0。存量侧的 14+15 个重复簇、12 个复杂度热点、40.11% erosion 是**已知存量**，按 issue 02 的设定只做度量不做闸门，本轮不算回归；它们该继续显示为高，直到后续轮次真的把它们降下来。
