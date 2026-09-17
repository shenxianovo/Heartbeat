# 05 决策记录与仓库卫生

Status: `resolved`
覆盖候选: `X-D3`（P2）、`X-D1`（P2）、`X-D2`（P2）、`X-C1`（P2）、`M-EX`（P2）、`M-03b`（M3 变体）

## 问题

1. **`X-D3` Hub 持久接管没有 ADR**：`2d86169` 让 Hub 接管注册与持久投递，这是架构级决定，只有一份 153 行的 `docs/hub-record-delivery.md`；而「窗口标题必须静置」这种小得多的决定有 `ADR-0008`。ADR 门槛不一致，以后回溯会踩空。
2. **`M-EX` main 上的病历没带过来**：`main:docs/agents/` 有 5 份，`engineering-friction.md`、`dotnet-refactoring.md` 没进新仓。这两份恰好记录了 M1/M2/M3 怎么发生的。缺了它们，新仓没有任何显式收口检查项去拦这几条模式——而 M2、M3 已经复发。
3. **`M-03b` 生产规则与探针规则各一份**：标题静置规则在 `src/Collectors/…/DesktopRecordProjector.cs:101-161` 和 `tools/Heartbeat.Dev/WindowTitleChurn.cs:138-176` 各写一遍。
4. **`X-C1` Developer CLI 4068 行、是生产代码的 34%**，职责从验证扩到环境编排、探针、数据库导出，自己没有边界约束。
5. **`X-D1` 已完成的 `.scratch` 没删**：`.scratch/window-title-churn/spec.md` 还在（`AGENTS.md:21` 要求做完就删），另有 `.scratch/frontend-timeline-status`、`.scratch/verification-foundation/issues` 两个空目录。

## 要做的

1. **补 `ADR-0009`**（编号接着来），记录「Hub 拥有注册与持久投递」这个决定：当时的问题、被否掉的选项、代价。素材在 `docs/hub-record-delivery.md` 与 `2d86169` 的提交信息里。照 `docs/adr/ADR-TEMPLATE.md` 的格式。
2. **把 ADR 门槛写清**：在 `AGENTS.md` 的 Design decisions 段落里加一句判据——什么级别的决定必须有 ADR（跨组件归属、协议语义、数据权威位置这类），什么不用。一句话，别写成流程手册。
3. **把病历带过来，形成收口检查清单**：不要照搬 main 的两份文档全文（那是 main 的语境）。在 `docs/agents/` 下新建一份简短的收口检查清单，把已经复发过的模式变成可勾的检查项，至少包含：
   - 新增兼容分支必须写退出条件（M2；本仓已复发于 `tools/Heartbeat.Dev/DatabaseReadings.cs`）
   - 同一契约新增第二处权威必须给对账手段（M3；本仓已复发于 `TimeMode`/`EndMode` 映射与协议 type 字符串）
   - 文档里不写会腐坏的快照数字（M1）
   - 「代码完成」不等于「需求完成」，验收要有证据（M5）
   并在 `AGENTS.md` 里引一句指向它。清单要短到每次收口都愿意看，控制在一屏内。
4. **`M-03b`：让探针复用生产规则**，而不是各写一份。若因为项目引用方向不允许（`tools` → `src/Collectors`），就把规则抽到双方都能引用的位置；如果判断代价过大，就在探针里显式标注「规则来源是 `DesktopRecordProjector`，改动必须同步」并加一条对账测试。选哪条都要说清理由。
5. **`X-C1`：给 CLI 划边界并写进 `docs/verification.md`**（注意：这个文件归 issue 02/04 那位 agent，本 issue 只提供文字，由那位合入，或者等他做完再改，别并发写）。边界至少回答：CLI 承担哪些能力、探针类一次性工具的去留标准、什么东西不该进 CLI。
6. **`X-D1`：删 `.scratch/window-title-churn/spec.md` 与两个空目录**。durable 的知识（标题静置规则）已经在 `ADR-0008` 和协议文档里，确认一遍再删。

## 边界

- 不要动 `docs/verification.md`（见第 5 条）。
- 不要动 `.scratch/implementation-review/`（用户正在按它逐条过）与 `.scratch/x-series-hardening/`（本轮工作单）。
- 中文表达按 `haohao-shuohua` 口径。

## 验收

- `ADR-0009` 存在且格式与既有 ADR 一致，`docs/hub-record-delivery.md` 顶部引用它。
- 收口清单存在、够短、每条都对应一个已发生过的真实模式（不要凭空造检查项）。
- `.scratch` 下只剩 `implementation-review/` 与 `x-series-hardening/`。
- 探针与生产规则的关系有明确交代（复用或对账测试）。

## Comments

### 2026-09-17 收口

六条都落了地：

1. `docs/adr/ADR-0009-delivery-belongs-to-hub.md`，事后追认 `2d86169`，否掉的两个替代方案（Collector 各自本地重试队列、后端离线接收缓冲）和当前 Hub 的可靠性缺口（C-01/C-02/C-04/C-09/S-11）都写在后果里；`docs/hub-record-delivery.md` 顶部已引用。
2. `AGENTS.md` 加了 ADR 门槛：跨组件责任归属、协议语义、数据权威位置这三类必须有 ADR。
3. `docs/agents/closeout.md`，六条检查项全部对应本仓或 main 上真实发生过的模式，控制在一屏内，`AGENTS.md` 里有指向。
4. `M-03b` 选的是「对账」而不是「复用」：`tools` → `src/Collectors` 的引用方向不该开，规则表抽到 `tests/window-title-dwell-scenarios.json`，Dev 侧与 Collector 侧各一条对账测试钉住两边不许漂移。
5. `X-C1` 的 CLI 边界由 issue 02/04 那位合入 `docs/verification.md`「Developer CLI 的边界」一节，本 issue 没并发改这个文件。
6. `.scratch/window-title-churn/spec.md` 与两个空目录已删；durable 的那部分知识（1.5 秒默认值的来历、怎么复核）迁到了 `src/Collectors/Heartbeat.Collector.Desktop.Mac/README.md`。

验收四条全过。`.scratch` 下现在只剩 `implementation-review/` 与 `x-series-hardening/`。
