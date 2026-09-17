# 03 文档与代码对账

Status: `resolved`
覆盖候选: `X-B1`（P1）、`X-B2`（P2）、`X-B4`（P3）、`X-B5`（P3）、`X-A6`（P3）、`X-E2`（P3）

## 问题

1. **`X-B1` 写死的测试数量已失真且互相矛盾**：`docs/hub-record-delivery.md:160`、`:163`、`docs/validation/system-capability-inventory.md:33`、`docs/validation/system-acceptance.md:58`、`docs/validation/experience-visualization.md:22` 各写了一个数字，彼此不一致，也和现在的真实数量不符。这是 main 上 M1（状态漂移）最典型的载体。
2. **`X-B2` 最大确认间隔默认值漂移**：代码 `CollectorOptions.cs:48-51` 与 `docs/protocols/desktop-application-foreground-v1.md:47` 是三倍，`ADR-0007:16,31`、`ADR-0005:49`、`docs/validation/system-capability-inventory.md:19`、`docs/validation/system-acceptance.md:11` 有的写两倍。
3. **`X-B4` 宣称已实测但没有证据**：`docs/development.md:151` 说「Web 通过真实 API 展示 Record」已实测，98 次 artifacts 里找不到对应场景证据。
4. **`X-B5` README 文档索引落后**：`README.md:16-17` 的索引没覆盖 `docs/protocols/` 的 5 份协议、`docs/validation/`、`docs/agents/`。
5. **`X-A6` 未实现的语义被领域语言先行承诺**：`CONTEXT.md:35`、`:63` 描述的 `ADR-0006`（结果修正）、`ADR-0003`（设备身份）语义还没实现，读文档的人会以为它们成立（`docs/recording-open-questions.md:45,55` 里有记录，但 CONTEXT 里没有标注）。
6. **`X-E2` 债务全靠文档承载**：`src`/`tests`/`tools`/`docs` 里 `TODO|FIXME|HACK|XXX|for now|workaround` 零命中，所有已知缺口都写在文档里（约 1928 行）。好处是集中，坏处是改到那一行的人看不到有个 open question 挂着。

## 要做的

- 删掉**所有**硬编码测试数量。要展示就描述性地说「见 `docs/verification.md` 的分层」，或者由工具生成；不要留一个会腐坏的快照数字。
- 确认代码里的实际倍数（以 `CollectorOptions.cs` 为准），把非 ADR 文档改对。**ADR 是历史记录，不要改原决策文字**——用 blockquote 注记说明实现取值与当时决策不同以及为什么（照 MEMORY 里 ADR-045/049 那个做法）。
- `docs/development.md:151` 那句话：没有证据就降级成待验证，别留一个没人验过的「已实测」。
- 补 README 文档索引。
- `CONTEXT.md` 里给未实现的语义加显式标注（例如「（语义已定，尚未实现，见 open questions）」），标注要短，别把 CONTEXT 写成状态看板。
- `X-E2`：在最关键的几处代码现场留一行指向文档的锚（比如 `Record.cs` 里结果修正相关位置、设备身份相关位置）。**不要**用 `TODO/FIXME/HACK` 这类词——那会污染 erosion 扫描；用陈述句注释指向 `docs/recording-open-questions.md` 的对应条目。挑 2~4 处最容易被误改的地方就够，不要全仓撒。

## 边界

- **不要**碰 `docs/verification.md`（issue 02 独占）。
- 不要碰 `tools/` 与 `src/` 下的逻辑代码；`X-E2` 允许只加注释，不改行为。
- 中文表达按 `haohao-shuohua` 的口径：不要 AI 味，不要翻译腔，句子短，别每段都加粗。技术材料里的章节编号和表格照旧保留。

## 验收

- `git grep -nE "[0-9]{2,4} (个|条)?(测试|test)" docs README.md CONTEXT.md` 之类的自查里，不再有会腐坏的数量快照。
- 倍数说法全仓一致（代码为准），ADR 原文未被改写、只加了注记。
- 改完自己过一遍：markdown 内部锚点没断，表格没坏。

## Comments

### 2026-09-17 实施记录

六条都落地了，没跑构建和测试，改动全在工作树。`docs/verification.md`、`tools/`、`.scratch/implementation-review/` 都没碰。

**`X-B1`（硬编码测试数量）**：四处快照数字全删，一个新数字都没换上。

- `docs/hub-record-delivery.md:163`：`237/237` 与 26/65/53/93 分层数、前端 15/9 改成「零失败零跳过，四层都在内」，数量指向 `docs/verification.md` 和 `.artifacts/verification/`。
- `docs/validation/system-capability-inventory.md:33`：`.NET 237、Vitest 15、Playwright 9` 改成三套都跑过、全绿，分层口径链到 `../verification.md`。
- `docs/validation/system-acceptance.md:57-59`：表格三行的 `237/237`、`15/15`、`9/9` 换成结果描述，表格结构没动；表下补一句指向 `../verification.md`。
- `docs/validation/experience-visualization.md:22`：`31 项 Vitest`、`15 项 Chromium` 去掉数字。
- 保留的：`system-acceptance.md:23,40` 的「3 项已修复」「8 项已修复」是那一轮审查缺陷数，属于历史事实不会腐坏；同文件 `7.005443 秒` 是某次冒烟的实测读数，同理保留。

**`X-B2`（确认间隔倍数）**：以 `CollectorOptions.cs:50-51` 为准，实际是 `intervalSeconds * 3`，代码注释给的理由是「连续两次缺失确认才算观察空白，一次晚到的 tick 属于调度抖动」。

- 非 ADR 文档直接改对：`docs/validation/system-acceptance.md:11` 两倍改三倍，并补上取三倍的原因。
- `docs/protocols/desktop-application-foreground-v1.md:47`、`system-capability-inventory.md:19`、`src/Collectors/…/README.md:21`、`ADR-0007:31` 本来就是三倍，未动。
- `ADR-0005:49`（2026-09-14 演进段）原文一字未改，下面加了一条 blockquote 注记，写明实现取值是三倍、与当时写法不同、以及为什么，并指到 `ADR-0007` 决策第 4 条。
- `ADR-0007:16` 复核后发现不是漂移：那句两倍在「背景」里，描述的是本决策之前的状态，第 31 行决策已经放宽到三倍。原文照旧，只在那条下面加了一句 blockquote 注记，免得读者把背景当现状。

**`X-B4`（宣称已实测）**：`docs/development.md:151` 拆开写——Hub 离线接管、恢复后写 PostgreSQL、watcher 热更新保留「已实测」；「Web 通过真实 API 展示 Record」降级为待验证，写明 `.artifacts/verification/` 里没有对应场景证据、端到端用例走的是 fixture。

**`X-B5`（README 文档索引）**：`README.md` 文档目录补三条协议（away、input、observation status，原来只列了 application 和 window），另加 `docs/validation/`（三份验收记录）和 `docs/agents/`（issue-tracker、triage-labels、domain、closeout）两条入口。`docs/agents/closeout.md` 目前还是未跟踪文件，链接已按最终路径写。

**`X-A6`（领域语言承诺未实现语义）**：`CONTEXT.md:63` 结果更正、`CONTEXT.md:79` Device Identity 各加一句括注「（语义已定，尚未实现，见 …）」，链到 `docs/recording-open-questions.md` 的「历史纠错与删除」和「设备关联」。只加括注，术语定义和 `_避免使用_` 行没动。

**`X-E2`（代码现场留锚）**：只加注释，行为零改动，没用 `TODO`/`FIXME`/`HACK`/`XXX`/`for now`/`workaround`，全仓词边界扫描仍是零命中。三处：

- `src/Backend/Heartbeat.Domain/Recording/Record.cs`：类上 XML 注释说明只有创建路径、没有更新方法是因为 ADR-0006 机制未设计，指向「历史纠错与删除」。
- `src/Collectors/…/DesktopProtocols.cs:10`：说明 value 里的 `deviceId` 是配置的 Target，不是领域里的 Device Identity，指向「设备关联」。
- `src/Collectors/…/PendingHubSubmissions.cs`：说明内存缓冲没有落盘和积压上限是有意边界，指向「Collector 到 Hub 的未交接数据」。

**自查**：全仓 grep 已无会腐坏的数量快照；倍数说法除两处 ADR 原文（各带注记）外一律三倍；改过的文档链接目标都存在，`system-acceptance.md` 表格两列结构完好，CONTEXT 的两个跨文档锚点对应真实标题。

**新发现、本轮没处理的漂移**：

1. `docs/validation/experience-visualization.md:22` 原写 31 项 Vitest / 15 项 Chromium，`system-acceptance.md` 同期写 15 / 9。两份同轮验收记录互相矛盾，说明当时至少有一份是从别处抄的。数字已按 `X-B1` 全部删掉，但两份记录覆盖范围到底是不是同一轮，没有证据可查，留给作者判断。
2. `docs/validation/system-capability-inventory.md:19` 表格里「30 秒快照」是 main 旧实现的取值，与当前 Collector 默认 5 秒采样间隔不是一个东西。该单元格描述的是旧实现列，读起来容易误当现状，但改它超出本单的范围。
3. `README.md:16` 把桌面前台应用协议称作「当前已实现的首个 Record 协议」，现在五份协议都实现了，「首个」这个说法已经过时。本轮只补索引条目，没改这句措辞。

### 2026-09-17 追加：收掉两条越界漂移

**`system-capability-inventory.md:19` 的「30 秒快照」**：核对后不是漂移。这一格在「main macOS」列，而该列由文档开头固定到 `86911e75459b0038eeba8aec623d2f3d2890a1f3`；`git show` 那个 commit 的 `AppMonitorService.SnapshotInterval` 确实是 `TimeSpan.FromSeconds(30)`。锚在固定 commit 上的历史取值不会腐坏，改成重写的值反而会把两代实现记混。

真正的缺口在旁边一列：「当前 macOS 重写」从头到尾没写自己的采样默认值，读者只看到一个 30 秒就容易当成现状。所以只动了重写列——采样间隔与断采阈值都注明由 [`CollectorOptions.cs`](../../src/Collectors/Heartbeat.Collector.Desktop.Mac/CollectorOptions.cs) 配置，默认 5 秒采样、断采阈值取采样间隔三倍；main 那格只把「30 秒快照」补成「30 秒快照循环」，说清它是循环周期。

同一份文档其余旧取值一并核对过，都属实、都在 main 列：`:13` 的点击门控「最近 1 秒」对应 `AppMonitorService.TitleGateWindow = 1s`，`:17` 的滚轮 `±120` 对应 `InputEventBuffer.WheelDelta = 120`。两处不改。

顺手补了一处漏写：`:13` 重写列原来没提标题站稳，而 `CONTEXT.md` 已把「站稳（Title Dwell）」立为领域术语，代码里也有 `window-title-dwell-ms`。补了一句「新标题要站稳一段时长才承认，时长由 `window-title-dwell-ms` 配置」，只写配置项名不写毫秒数。

**README「首个」**：`README.md:16` 原写「当前已实现的首个 Record 协议」，`docs/protocols/` 现在有五份协议，这个说法已经过期。改成描述协议本身的内容——「前台应用读数的 value 结构与区间断开规则」，不再依赖实现顺序，以后加协议也不会腐坏。全仓已无其他「首个/首份」式的排序说法。

**Vitest / Chromium 矛盾数字**：确认已在上一轮按 `X-B1` 全删，`experience-visualization.md:22` 与 `system-acceptance.md:57-59` 现在都没有数量快照，无需再动。

**复查**：全仓再 grep 一遍，`237`、`31 项`、`15 项`、`9 项`、`Vitest N`、`Chromium N` 均零命中；`30 秒` 只剩 main 列那一处历史取值。能力清单表仍是四列，两行改动后列数与分隔符未变，新增的 `CollectorOptions.cs` 链接路径已确认存在。仍未 commit，`docs/verification.md` 未碰。
