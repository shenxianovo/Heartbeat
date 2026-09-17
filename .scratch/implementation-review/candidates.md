# Heartbeat 实现体检：候选清单

- 基线：`cleanroom-rewrite@27cdf6f`，对照 `main@86911e7`
- 体检日期：2026-09-17
- 范围：前端、后端、Hub 与 macOS Collector、测试与文档与 Developer CLI，外加一轮「有没有重犯 main 的病」纵向对照
- 性质：只读体检。没有改任何代码，没有提交，没有跑破坏性命令

> **2026-09-17 更新**：X 分区（横切）已经做完一轮，工作单在 `.scratch/x-series-hardening/`。逐条状态见下面「横切」小节前的说明。C / S / W 三个分区按作者判断顺延，本文其余内容仍是 `27cdf6f` 时的原始体检结论。


## 怎么用这份清单

每条候选有一个稳定编号，按面分区：

- `C-xx` 采集与投递（Collector + Hub）
- `S-xx` 后端（API / Application / Infrastructure）
- `W-xx` 前端（Heartbeat.Web）
- `X-xx` 横切（测试证明力、文档一致性、Developer CLI、仓库规范）
- `M-xx` 与 main 腐坏模式的对照结论

严重度回答的是「值不值得现在停下来看」，不是线上故障等级：

- **P1**：现在就是错的，或者会静默丢数据，看完就该决定动不动手
- **P2**：会咬人，但要么有条件、要么只在特定路径上
- **P3**：架构与卫生问题，攒着一起收拾更划算

每条都给了 `文件:行号`，坐标落在 `27cdf6f`。路径按惯例省了公共前缀：后端类名路径在 `src/Backend/` 下，Hub 在 `src/Hub/`，Collector 在 `src/Collectors/`，前端 `src/` 开头的在 `src/Frontend/Heartbeat.Web/` 下。你报编号，我就展开那一条：先复核，再谈改不改。**清单里相当一部分是静态阅读得出的，没有跑起来验过**，我在每条标了「复核」一栏说明这点。

## 结论先说

**没有像 main 那样腐坏。** 这不是客套话，是量出来的：

| | `main@86911e7` | `cleanroom-rewrite@27cdf6f` |
|---|---|---|
| 文件数 | 约 1270 | 317 |
| C# 文件 | 648 | — |
| Markdown | 266（`.scratch/` 占 144） | 约 1928 行文档 |
| ADR | 59 份，且连环 supersede | 8 份，无 supersede |
| 最大运行时文件 | `CollectorRuntime.ManagedProcess.cs` 2147 行 | `MacAccessibilityNative.cs` 510 行 |
| EF migration | 86 个 | — |
| `TODO/FIXME/HACK/for now/workaround` | 有 | `src`/`tests`/`tools`/`docs` 全仓零命中 |

从 main 身上提炼出九个腐坏模式（详见 `M-` 分区），新实现里**七个避开了**，两个已经复发，但都还很小：

- 复发一：Dev 工具里留着「拆 Track 之前」的旧数据形态兼容分支（`C-13` / `M-02`）。这正是 main 上「兼容债务扩散、没有退出条件」的第一颗种子。
- 复发二：同一个契约有多处可漂移的权威来源（`S-07`、`C-14` / `M-03`）。这是本轮最重的一条老病。

**但腐坏不是当前最要紧的事。** 七条 P1 里有五条与腐坏无关，是「现在就是错的」：

| 编号 | 严重度 | 一句话 |
|---|---|---|
| `C-01` | P1 | 正常停机没有 flush：SIGINT 立即停、SIGTERM 完全不处理，内存里的 pending 和开着的区间静默消失 |
| `C-02` | P1 | 后端映射阶段的永久错误被当成「没确认」，无限重试且不进 `/failures` |
| `C-04` | P1 | 输入事件 1:1 成 Record，与 10000 容量、每轮 500 条的吞吐不匹配，打满之后整链停摆 |
| `W-01` | P1 | 跨日范围下所有时间标签只有「时:分」，「次日」是硬编码的 |
| `X-A1` | P1 | 结构质量闸门几乎总是 `--base HEAD`，只拦增量、不度量已提交的债务；唯一一次跨基点度量直接失败 |
| `X-A2` | P1 | 真实 JWT 验签链路零测试覆盖，集成测试里的 401 断言证明的是测试替身 |
| `X-B1` | P1 | 多份文档写死了测试数量快照，现在已经失真而且互相矛盾 |

另外有一条元层面的观察值得单独说：**你自己在 main 上写的病历没带过来**（`M-EX`）。`docs/agents/engineering-friction.md`、`dotnet-refactoring.md` 留在旧仓，新仓的 `docs/agents/` 只带了 domain / issue-tracker / triage-labels。结果是 M2、M3 这两条你已经吃过苦头的模式，在新仓里没有任何显式的收口检查项——它们复发的时候，没有东西会拦。

## 全部候选一览

### 采集与投递

| 编号 | 严重度 | 标题 |
|---|---|---|
| `C-01` | P1 | 正常停机没有 flush，内存 pending 与开区间静默丢失 |
| `C-02` | P1 | 映射阶段的永久错误无限重试，且不进 `/failures` |
| `C-03` | P2 | `failure` 是终态，但 Hub 仍回 `accepted` 并继续吞掉该 ID 的后续进度 |
| `C-04` | P1 | 输入事件 1:1 成 Record，与队列容量与吞吐不匹配 |
| `C-05` | P2 | Away 区间在持续期间从不延长 |
| `C-06` | P2 | `_heldKeys` 永不过期，漏一次 key-up 该键就永久失效 |
| `C-07` | P2 | Cocoa 回调没有异常边界，且在主 run loop 上做同步 AX 读与最长 2 秒等停 |
| `C-08` | P2 | 全仓没有 autorelease pool |
| `C-09` | P2 | UploadWorker 的未预期异常会把整个 Hub 拉停 |
| `C-10` | P3 | 固定 5 秒重试、无退避、无永久错误分类 |
| `C-11` | P3 | Observation Status 区间会跨越观测空白继续延长 |
| `C-12` | P3 | `Accept` 在 deferred 事务里先读后写，并发下 `SQLITE_BUSY` 变 503 |
| `C-13` | P3 | Dev 工具留着拆 Track 之前的兼容分支，违反净室约束 |
| `C-14` | P3 | 协议 type 字符串在四处各写一份 |

### 后端

| 编号 | 严重度 | 标题 |
|---|---|---|
| `S-01` | P2 | `value` 直接 CAST 成 jsonb，未校验可表示性，异常载荷 500 并卡死队列头 |
| `S-02` | P2 | OIDC access token 完全不校验 audience，配置为空即静默关闭 |
| `S-03` | P2 | Session scheme 对 token 形态零约束，OIDC 严、session 宽 |
| `S-04` | P2 | 真实认证管线在集成测试里被整体替换 |
| `S-05` | P3 | `Collector` 聚合的工厂与更新方法在生产路径不被调用，不变量落在死代码上 |
| `S-06` | P3 | 结束时间形状规则两处实现，Infrastructure 用 `ArgumentException` 表达业务校验 |
| `S-07` | P3 | `TimeMode`/`EndMode` 的枚举与字符串映射散在五处 |
| `S-08` | P3 | 区间交叠用 `ended_at > from`，零长度 Record 在窗口下界会漏 |
| `S-09` | P3 | 重放窗口的 `ended_at` 过滤走不了索引 |
| `S-10` | P3 | 时间精度契约缺失，Postgres 微秒截断由客户端自行补偿 |
| `S-11` | P3 | 400 响应的 `code` 不统一，而 Hub 的永久失败判定依赖 `code` |
| `S-12` | P3 | Endpoints 承担了游标编解码、DTO 逐字段复制、直连 EF |
| `S-13` | P3 | 时间参数不要求 offset，缺 offset 按服务器本地时区解释 |
| `S-14` | P3 | 「非 Point Track」用空结果加事后复查表达，端口语义不自洽 |
| `S-15` | P3 | 批量上传是 N 次串行往返，每条都开关一次连接 |

### 前端

| 编号 | 严重度 | 标题 |
|---|---|---|
| `W-01` | P1 | 跨日范围下时间标签只有「时:分」，「次日」硬编码 |
| `W-02` | P2 | 细粒度密度 tile 永久留在缓存且优先级最高，刷新按钮改不动它 |
| `W-03` | P2 | 日期步进把自定义范围静默压回单日 |
| `W-04` | P2 | 一个 Track 出错清空整条时间线；勾选变化让整窗重下 |
| `W-05` | P2 | `fetchAllRecords` 没有任何上限 |
| `W-06` | P2 | 空记录说成「这段时间很安静」，与自家领域语言冲突 |
| `W-07` | P2 | 通用 point 泳道被硬编码播报成「输入密度曲线」 |
| `W-08` | P2 | 概览把点观测按查询桶宽画成实心区间块 |
| `W-09` | P2 | 渲染期从 QueryClient 读缓存，memo 链失效且是非响应式读取 |
| `W-10` | P2 | `ReplayWorkbench` 已经是页面级 God Component |
| `W-11` | P2 | 后端响应零校验，契约漂移会静默变成 NaN 几何 |
| `W-12` | P3 | `trackLabel` 寄生在已废弃的 `TrackPicker` 里 |
| `W-13` | P3 | 「这条泳道要不要显示密度」有两份实现，粒度还不一样 |
| `W-14` | P3 | 组件靠 DOM data 属性通信，还有一批没有消费者的属性 |
| `W-15` | P3 | 401 只有详情面板处理，会话过期后页面无法自救 |
| `W-16` | P3 | 一组细节层面的领域语言与兜底偏差 |

### 横切

| 编号 | 严重度 | 标题 |
|---|---|---|
| `X-A1` | P1 | 质量闸门在 `--base HEAD` 下不度量已提交债务，唯一一次跨基点度量失败 |
| `X-A2` | P1 | 真实 JWT 验签零覆盖，401 断言证明的是替身 |
| `X-A3` | P2 | 前端组合层唯一的证明是 fixture 化浏览器测试加 CSS class 几何断言 |
| `X-A4` | P2 | `scenario delivery` / `replay-fixture` 是第一层测试的子集重跑 |
| `X-A5` | P2 | 测试重复被量到 63 处，但这份报告不进闸门也无人消费 |
| `X-A6` | P3 | ADR-0006 / ADR-0003 的不变量没有测试，因为还没实现，但领域语言已先行承诺 |
| `X-B1` | P1 | 多份文档写死测试数量快照，已失真且互相矛盾 |
| `X-B2` | P2 | 最大确认间隔默认值：两份文档说两倍，协议文档与代码是三倍 |
| `X-B3` | P2 | jscpd 观察报告实际参与闸门，与文档说法矛盾，且失败原因无诊断价值 |
| `X-B4` | P3 | `development.md` 宣称已实测真实链路，但没有对应场景与证据 |
| `X-B5` | P3 | README 文档索引落后于已实现协议 |
| `X-C1` | P2 | Developer CLI 已达 4068 行，是生产代码的 34%，职责扩到环境编排与探针 |
| `X-C2` | P2 | 复杂度闸门只拦增量，Erosion / coupling / LOC 全是观察，12 个热点可长期驻留 |
| `X-C3` | P2 | 基线物化依赖临时 worktree 加 `npm ci`，工具自身失败占历史失败的大半 |
| `X-C4` | P3 | `artifacts prune` 默认参数基本是空操作，证据目录 2.7G、`.local` 12G |
| `X-C5` | P3 | `SourceMetrics` 兜底分类把 `src` 外一切归为 tooling，Dockerfile 计入生产 LOC |
| `X-D1` | P2 | 已完成的 `.scratch` 没删，还残留两个空目录 |
| `X-D2` | P2 | issue tracker 与 triage labels 两份流程文档零使用 |
| `X-D3` | P2 | Hub 持久接管这个架构级决定没有 ADR，窗口标题静置却有 |
| `X-D4` | P3 | 验证契约文档改动不触发任何检查 |
| `X-E1` | P3 | 生产代码已有 7 处重复簇 |
| `X-E2` | P3 | 文档体量，以及「债务全靠文档承载」的结构性风险 |
| `M-EX` | P2 | 你自己在 main 上写的病历没带进新仓 |

---

## C 采集与投递

整体印象：SQLite outbox 的骨架、回执核对、应用与窗口的拆分、标题静置，这些都有行为测试兜着，主干是稳的。薄弱面集中在**边界和终态**——停机、永久失败、队列打满、区间收尾。

### C-01 正常停机没有 flush（P1）

- 机制：`Program.cs` 只挂了 SIGINT，收到就立即停；SIGTERM 完全没处理。`DesktopCollectorSession` 停下来的时候不做 flush，`PendingHubSubmissions` 里在内存的 pending 和还开着的区间就这么没了——不是投递失败，是根本没进 outbox，所以事后在 `/failures` 里也查不到。
- 证据：`Collectors/…/Program.cs:76-98`、`Program.cs:117-120`、`DesktopCollectorSession.cs:80-94`、`:96-100`、`:102-118`、`PendingHubSubmissions.cs:20-21`
- 方向：停机路径上补一次显式 flush 加开区间收尾，并把 SIGTERM 纳进来；要不要在 flush 超时的时候落盘成 failure，是个需要单独定的决策。
- 复核：静态可判，但「到底丢多少」需要跑一次 kill 实验。

### C-02 映射阶段的永久错误无限重试（P1）

- 机制：`RecordUploader` 把后端的映射阶段错误一律当成「没确认」，于是无限重试，也不进 `/failures`。文档 `docs/hub-record-delivery.md:96` 说的是永久失败要落 failure，代码没做到。
- 证据：`Hub/RecordUploader.cs:84`、`:89`、`:95`、`:101`、`:34-38`、`:141-142`、`:211-231`、`:49-56`、`docs/recording-api.md:69`、`docs/hub-record-delivery.md:86`、`:96`、`tests/Heartbeat.Hub.Tests/RecordUploaderTests.cs:218`
- 方向：与 `S-11` 一起改——后端把 400 的 `code` 统一，Hub 才有可靠的永久失败判据。
- 复核：静态可判。这条和 `S-11` 是同一枚硬币的两面，建议连排。

### C-03 `failure` 是终态，但 Hub 仍回 `accepted`（P2）

- 机制：一条记录被判定 `failure` 之后就是终态，可 Hub 对这个 ID 的后续提交仍然返回 `accepted`，于是该 ID 之后的所有进度被静默吞掉。调用方以为写进去了。
- 证据：`Hub/RecordOutbox.cs:77-82`、`:86`、`:89-97`、`Hub.Host/Program.cs:56-57`、`:62-72`、`docs/hub-record-delivery.md:96`、`tests/Heartbeat.Hub.Tests/RecordOutboxTests.cs:95-106`
- 方向：终态要么可复活，要么在 Accept 时明确拒绝并让上游知道。现在这个「假 accepted」是最糟的第三种。
- 复核：静态可判。

### C-04 输入事件 1:1 成 Record，与容量和吞吐不匹配（P1）

- 机制：每个输入事件都投一条 Record，队列容量 10000、每轮最多传 500 条。正常打字速率下就能把队列顶满，顶满之后整条链停摆——不只是输入，是所有 Track 一起卡。
- 证据：`DesktopRecordProjector.cs:190-208`、`Native/MacInputNativeEventTranslator.cs:69-79`、`Hub/RecordOutbox.cs:8`、`:55-56,72-75`、`:89-97`、`Hub.Host/HubSettings.cs:25-34`、`Hub.Host/Program.cs:82-86`、`DesktopCollectorSession.cs:112-116`、`PendingHubSubmissions.cs:20-21,36-74`、`docs/hub-record-delivery.md:102`
- 方向：这是个建模问题不是调参问题。要么在 Collector 侧就按时间桶聚合成密度记录，要么明确「输入事件是可丢的采样」并给背压策略。调大容量只是把撞墙时间往后推。
- 复核：需实测。跑一段真实打字，看队列水位曲线。

### C-05 Away 区间在持续期间从不延长（P2）

- 机制：进入 Away 之后不再延长该区间，采样期间完全空转。真实后果是：Away 期间进程结束，会留下一个零长度记录。
- 证据：`DesktopRecordProjector.cs:49-57`、`:61-64`、`:163-174`、`:176-188`、`docs/protocols/desktop-system-away-v1.md:5`
- 方向：Away 应该和其他持续区间一样按心跳延长，或者协议里明确写清「Away 只记起点」。
- 复核：静态可判。

### C-06 `_heldKeys` 永不过期（P2）

- 机制：漏掉一次 key-up（切前台、权限抖动、事件被吞都可能），那个键就永久留在 `_heldKeys` 里，之后每次按下都被当成重复而静默丢弃。用户视角是「某个键从此不算输入了」，而且不会自己恢复。
- 证据：`DesktopRecordProjector.cs:194-203`、`:243-245`、`Native/MacInputMonitoringNative.cs:170-175`、`Native/MacInputNativeEventTranslator.cs:27-53`
- 方向：给 held 状态加过期，或者在焦点切换与监听重启的时候清空。
- 复核：静态可判，构造重放很容易。

### C-07 Cocoa 回调没有异常边界，且在主 run loop 上同步等停（P2）

- 机制：通知回调里没有异常边界，异常穿过原生边界的行为是未定义的。更麻烦的是在 AppKit 主 run loop 上做同步 AX 读，还带最长 2 秒的等停——这 2 秒里主 run loop 是不动的。
- 证据：`Native/CocoaWorkspaceNative.cs:107-115`、`Native/MacAccessibilityNative.cs:99-108`、`:296-328`、`Native/MacInputMonitoringNative.cs:154-215`、`:209-213`、`MacSystemObservationSource.cs:91-138`、`:196-236`、`MacRunLoop.cs:15-24`
- 方向：回调最外层套 try/catch 并计数；AX 读挪出主 run loop，或者把等停降到毫秒级。
- 复核：需实测。挂一个会卡的 AX 目标才能看到真实影响。

### C-08 全仓没有任何 autorelease pool（P2）

- 机制：NSWorkspace、NSString 这些产生的自动释放对象，在非 run-loop 线程上没有 pool 接住，会一直累积。
- 证据：`Native/CocoaWorkspaceNative.cs:55-66`、`:188-196`、`MacRunLoop.cs:15-24`、`DesktopCollectorSession.cs:174-189`、`MacSystemObservationSource.cs:32-40,91-138`、`MacAccessibilityNative.cs:54-71,206-235`、`MacInputMonitoringNative.cs:142-151`
- 方向：在每个进原生的入口套 pool。
- 复核：需实测。长跑一天看 RSS 曲线，才知道是理论问题还是真漏。

### C-09 UploadWorker 的未预期异常会把整个 Hub 拉停（P2）

- 机制：BackgroundService 里逃逸的异常按 .NET 默认行为会终止 host，而 Hub 的接收端点和 worker 同进程，于是「传不出去」升级成「也收不进来」。
- 证据：`Hub.Host/UploadWorker.cs:19-26`、`Hub.Host/Program.cs:1-94`、`:35`、`Hub/RecordOutbox.cs:113-116`、`:129-132`、`Hub/RecordUploader.cs:141-142`
- 方向：worker 循环最外层兜住并记错，接收面不能被投递面拖死。
- 复核：静态可判。

### C-10 固定 5 秒重试、无退避、无永久错误分类（P3）

- 机制：被吊销的 API key 会被以 5 秒一次的节奏永久敲打，日志刷满，也不会升级成需要人工介入的状态。
- 证据：`Hub.Host/UploadWorker.cs:28`、`Hub/ApiKeyTokenProvider.cs:79-110`、`Hub/RecordUploader.cs:108-125`、`docs/hub-record-delivery.md:96`
- 方向：退避加错误分类，和 `C-02`、`S-11` 一起做。
- 复核：静态可判。

### C-11 Observation Status 区间会跨越观测空白继续延长（P3）

- 机制：其它 Track 遇到观测空白都会断开，Observation Status 不会。它恰恰是那条「我到底在不在观测」的 Track，语义上最不该跨空白。
- 证据：`DesktopRecordProjector.cs:72-80`、`:210-216`、`:296-305`、`MacSystemObservationSource.cs:232-236`、`docs/protocols/desktop-observation-status-v1.md:5,18`
- 方向：与其他 Track 统一断开规则。
- 复核：静态可判。

### C-12 `Accept` 在 deferred 事务里先读后写（P3）

- 机制：deferred 事务里先读后写，并发提交下会撞 `SQLITE_BUSY`，对外表现成 503 抖动。
- 证据：`Hub/RecordOutbox.cs:23-28`、`:52-56`、`:77-82`、`:89-109`、`:135-162`、`Hub.Host/Program.cs:82-86`
- 方向：改 immediate 事务，或者重试封在 store 内部而不是漏成 503。
- 复核：需实测。并发压一下才知道概率。

### C-13 Dev 工具留着拆 Track 之前的兼容分支（P3，也是 `M-02`）

- 机制：`DatabaseReadings` 里有一段兼容「拆 Track 之前」的旧数据形态。净室重写的前提是没有旧数据，`AGENTS.md:5-9` 也写明了这条约束。它现在只有一处、只在工具层，但这正是 main 上兼容债务扩散的起手式。
- 证据：`tools/Heartbeat.Dev/DatabaseReadings.cs:84-85`、`:104-112`、`:120`、`tests/Heartbeat.Dev.Tests/DatabaseReadingsTests.cs:14`、`AGENTS.md:5-9`
- 方向：删掉，连测试一起。趁只有一处的时候删最便宜。
- 复核：静态可判。

### C-14 协议 type 字符串各写一份（P3，也是 `M-03`）

- 机制：同一个协议 type 字符串在 Collector 常量、Dev 的 SQL、前端 registry、各层测试里各写一份，四份都能独立漂移。
- 证据：`Collectors/…/DesktopProtocols.cs:10-21`、`tools/Heartbeat.Dev/DatabaseReadings.cs:95,101,110`、`src/Frontend/Heartbeat.Web/src/components/records/renderers/registry.ts`、`tests/Heartbeat.Collector.Desktop.Mac.Tests/DesktopRecordProjectorTests.cs:8-9`、`tests/Heartbeat.Integration.Tests/*`、`tests/Heartbeat.Hub.Tests/QueueFixture.cs:32`
- 方向：跨语言边界注定要有两份（C# 与 TS），那就让其中一份可校验——比如拿协议文档或 C# 常量当权威，加一个对账检查。
- 复核：静态可判。

---

## S 后端

先说两件**核对通过**的事，免得清单看起来一片红：

- **Owner 隔离逐端点核对过了**，没找到能读到别人 Timeline 的路径。证据：`PostgresCollectorRegistrationStore.cs:14-24`、`PostgresTrackStore.cs:23-40`、`:50-71`、`PostgresRecordReplayStore.cs:13-20`、`PostgresPointRecordCountStore.cs:14-21`、`UploadRecords.cs:75`、`PostgresRecordStore.cs:13-18`、`RecordReplayHttpTests.cs:161-184`
- **后端的领域语言没有明显漂移**，命名和 CONTEXT 对得上；集成测试真连 Postgres 跑（`PostgresFixture.cs:11-19`）。

### S-01 `value` 直接 CAST 成 jsonb（P2）

- 机制：`value` 不做 jsonb 可表示性校验就 CAST。带 `\u0000` 之类 Postgres 不接受的载荷会打成 500，而 Hub 把 500 当可重试，于是这一条永远排在队列头上重试，后面全堵住。
- 证据：`Heartbeat.Infrastructure/Persistence/PostgresRecordStore.cs:30`、`:83`、`Heartbeat.Domain/Recording/JsonValue.cs:9-14`、`docs/recording-api.md:145`、`UploadRecords.cs:98`、`Hub/RecordUploader.cs:211-231`
- 方向：入口校验并返回 400 加稳定 `code`，让它成为永久失败而不是队头堵塞。
- 复核：需实测。构造一个含 `\u0000` 的载荷打一次就知道。

### S-02 OIDC access token 不校验 audience（P2）

- 机制：audience 校验在配置为空的时候被静默关掉，`appsettings.json:11` 就是空的。等于任何该 issuer 签给别的 client 的 token 都能进来。
- 证据：`Heartbeat.Api/Authentication/AuthenticationExtensions.cs:48`、`:52`、`:65-75`、`Heartbeat.Api/appsettings.json:11`
- 方向：缺配置就启动失败，别静默降级。安全开关不该有默认关。
- 复核：静态可判，但值得配合 `X-A2` 一起补真实验签测试。

### S-03 Session scheme 对 token 形态零约束（P2）

- 机制：按 `typ` 路由，OIDC 那条严、session 那条宽，形成不对称。宽的那条对 token 形态没有约束。
- 证据：`AuthenticationExtensions.cs:57`、`:86-96`、`appsettings.json:6-8`、`OwnerClaims.cs:9-10`
- 方向：两条 scheme 的校验强度对齐，或者写清为什么可以不对齐。
- 复核：静态可判。

### S-04 真实认证管线在集成测试里被整体替换（P2）

- 机制：`RecordingApiFactory` 把整条认证管线换成测试 handler，于是 `S-02`、`S-03` 这类配置层缺陷不可能被测试发现。与 `X-A2` 是同一件事的两个视角。
- 证据：`tests/Heartbeat.Integration.Tests/RecordingApiFactory.cs:38-47`、`AuthenticationContractTests.cs:9-40`
- 方向：留一组走真实 handler 的用例（自签 JWKS 就够），验 issuer、audience、过期、错 client。
- 复核：静态可判。

### S-05 `Collector` 聚合的工厂与更新方法不被调用（P3）

- 机制：聚合上写了不变量，但生产路径根本不走那些方法，注册直接落库。不变量待在死代码里，只有单测在用。
- 证据：`Heartbeat.Domain/Recording/Collector.cs:23-56`、`:58-61`、`Heartbeat.Application/Recording/RegisterCollector.cs:50-60`、`:57`、`PostgresCollectorRegistrationStore.cs:19-24`、`Timeline.cs:38-41`、`CollectorTests.cs`
- 方向：要么让生产路径走聚合，要么删掉这层假保护。测试绿但生产不设防是最坏的组合。
- 复核：静态可判。

### S-06 结束时间形状规则两处实现（P3）

- 机制：同一条规则在 Domain 和 Infrastructure 各写一遍，而 Infrastructure 那份用 `ArgumentException` 表达业务校验——异常类型选错会让上层没法正确分类（参见 MEMORY 里 Heartbeat 上一轮踩过的同类坑）。
- 证据：`Heartbeat.Domain/Recording/Track.cs:53-65`、`PostgresRecordStore.cs:19-26`、`:96-99`、`UploadRecords.cs:98-106`、`RecordEndpoints.cs:173-177`、`docs/recording-api.md:167-176`
- 方向：规则归 Domain 一份，Infrastructure 只信任已校验的输入。
- 复核：静态可判。

### S-07 `TimeMode`/`EndMode` 映射散在五处（P3，也是 `M-03`）

- 机制：枚举与字符串的映射有五处独立实现，任何一处漂移都会造成读写不一致。这是本轮 `M-03` 判定最重的依据。
- 证据：`Heartbeat.Api/Endpoints/RecordEndpoints.cs:210-223`、`Heartbeat.Api/Endpoints/TrackEndpoints.cs:113-126`、`:128-141`、`Configurations/TimeModeConverter.cs:15-27`、`Configurations/EndModeConverter.cs:15-27`、`PostgresRecordStore.cs:22-26`、`Configurations/TrackConfiguration.cs:17-21`
- 方向：收敛成一处映射，其余全部引用它。
- 复核：静态可判。

### S-08 区间交叠用 `ended_at > from`（P3）

- 机制：严格大于会让零长度 Record 在窗口下界被漏掉，而零长度记录是真会出现的（见 `C-05`）。
- 证据：`PostgresRecordReplayStore.cs:33-37`、`Collectors/…/DesktopRecordProjector.cs:224-226`、`docs/protocols/desktop-observation-status-v1.md:5`、`RecordConfiguration.cs:13-15`、`docs/recording-api.md:195`
- 方向：先定「零长度记录算不算落在窗口里」，再改比较符号。
- 复核：静态可判。

### S-09 重放窗口的 `ended_at` 过滤走不了索引（P3）

- 机制：过滤条件用不上现有索引，首页查询要从 Track 起点顺扫并逐行回表。数据量小时看不出来，长期会变成首屏慢。
- 证据：`PostgresRecordReplayStore.cs:33-43,54-58`、`Configurations/RecordConfiguration.cs:26-28`、`docs/recording-storage-model.md:219-220`、`src/Frontend/Heartbeat.Web/src/api/queries.ts:105-107`
- 方向：改成可用索引的范围表达，或者加覆盖索引。
- 复核：需实测。灌数据看执行计划。

### S-10 时间精度契约缺失（P3）

- 机制：Postgres 存到微秒会截断，Hub 自己在客户端复刻了一份补偿逻辑。精度契约没写在协议里，靠两边默契。
- 证据：`src/Hub/Heartbeat.Hub/RecordUploader.cs:205-209`、`Heartbeat.Domain/Recording/Record.cs:47-49,63`、`docs/recording-api.md:141-146,150-165`
- 方向：把精度写进 API 契约，让服务端成为唯一的截断方。
- 复核：静态可判。

### S-11 400 响应的 `code` 不统一（P3，与 `C-02` 连排）

- 机制：Hub 的永久失败判定依赖 `code`，而后端的 400 有的带、有的不带、带的也不一致。判定因此不可靠。
- 证据：`RecordEndpoints.cs:206-208`、`tests/Heartbeat.Integration.Tests/RecordUploadHttpTests.cs:281-292`、`:385-391`、`Hub/RecordUploader.cs:211-231`、`docs/recording-api.md:148`
- 方向：给 400 定一套稳定 `code` 并写进契约，Hub 按 `code` 分类。
- 复核：静态可判。

### S-12 Endpoints 承担层外职责（P3）

- 机制：游标编解码、逐字段 DTO 复制、直连 EF 都堆在 Endpoints 里。有架构测试，但拦不住这类越界。
- 证据：`RecordEndpoints.cs:63-73`、`:123-133`、`:168-169`、`:225-288`、`:294-299`、`UploadRecords.cs:6-11`、`Heartbeat.Api/Program.cs:4,33-38`、`ArchitectureTests.cs:5-11`
- 方向：游标是领域概念，值得单独一个类型；DTO 复制交给映射；EF 只在 Infrastructure 出现。
- 复核：静态可判。

### S-13 时间参数不要求 offset（P3）

- 机制：缺 offset 时按服务器本地时区解释。同一个请求在不同部署环境下含义不同。
- 证据：`RecordEndpoints.cs:31-33,95-97`、`:263-265`、`:296-299`、`docs/recording-api.md:186-187,242`
- 方向：强制带 offset，缺了就 400。
- 复核：静态可判。

### S-14 「非 Point Track」用空结果加事后复查表达（P3）

- 机制：端口返回空结果，再由调用方事后复查 Track 类型来区分「真的没有」和「这个 Track 不该问」。语义不自洽。
- 证据：`PostgresPointRecordCountStore.cs:26-29`、`Heartbeat.Application/Recording/CountPointRecords.cs:91-93`
- 方向：端口层面就区分这两种情形。
- 复核：静态可判。

### S-15 批量上传是 N 次串行往返（P3）

- 机制：批量接口内部是 N 次串行，每条都开关一次连接。和 `C-04` 叠在一起会更难受。
- 证据：`UploadRecords.cs:83-115`、`:105`、`PostgresRecordStore.cs:64-69,110-116`、`docs/recording-storage-model.md:250`
- 方向：一个事务一次连接批写。
- 复核：需实测。测吞吐才知道值不值得现在做。

---

## W 前端

整体印象：时间几何、密度分层、重采样守恒这些核心算法质量不错，也有单测。风险集中在 `ReplayWorkbench` 这个编排层、缓存失效策略，以及领域语言漂移。

### W-01 跨日范围下时间标签只有「时:分」（P1）

- 机制：格式化只输出「时:分」，「次日」是硬编码的标记。跨日范围下用户看到两个 `03:00` 分不出是哪天，跨两天以上更是直接错。
- 证据：`src/components/replay/timeRange.ts:59-66`、`src/components/replay/ActivityOverview.tsx:181-187`、`src/components/filters/DateRangeControls.tsx:60-62`、`TimelineViewport.tsx:257-261`、`:356-359`
- 方向：格式化按当前范围跨度决定粒度，别在渲染点写死。
- 复核：静态可判，界面上一眼能验。

### W-02 细粒度密度 tile 永久留在缓存且优先级最高（P2，接近 P1）

- 机制：细粒度 tile 的 `staleTime` 是 Infinity，合并时优先级又最高，于是「刷新」按钮换不掉它。屏幕上是旧数据，而用户没有恢复手段。
- 证据：`src/api/queries.ts:54-55`、`:58-64`、`ReplayWorkbench.tsx:144-149`、`src/components/replay/densitySeries.ts:45-52`、`:159-173`、`densityTiles.ts:22-29`
- 方向：刷新要能失效 tile，或者 tile 带上数据版本参与 key。
- 复核：需实测。手动改一条记录再点刷新就知道。

### W-03 日期步进把自定义范围静默压回单日（P2）

- 机制：处在自定义范围时点一下前后翻页，范围被悄悄压成单日，而界面上看不出当前是自定义范围。
- 证据：`src/components/replay/ReplayWorkbench.tsx:138-143`、`:192-195`
- 方向：步进保持跨度；界面上显式标出「自定义」。
- 复核：静态可判。

### W-04 一个 Track 出错清空整条时间线（P2）

- 机制：并行查询里任一 Track 失败，整条时间线被清空；来源勾选变化又会让整窗数据全部重下。局部故障升级成全局白屏。
- 证据：`src/api/queries.ts:12-18,86`、`:85-113`、`ReplayWorkbench.tsx:239-255`、`src/app/providers.tsx:17-20`
- 方向：按 Track 隔离失败，勾选变化只影响增量。
- 复核：静态可判。

### W-05 `fetchAllRecords` 没有任何上限（P2）

- 机制：没有页数、条数、时间跨度上限，选一个大范围就串行拉全量，拉完才渲染。
- 证据：`src/api/client.ts:74-90`、`src/components/filters/DateRangeControls.tsx:36-55`、`ReplayWorkbench.tsx:239-240`
- 方向：设上限并明确「超出就要求缩小范围」，或者改成流式渐进渲染。
- 复核：需实测。

### W-06 空记录说成「这段时间很安静」（P2）

- 机制：这句话把「没观测到」说成了「什么都没发生」，与同一页面自己的说明以及 `CONTEXT.md:71-72` 的 Observation Gap 语义冲突。
- 证据：`src/components/replay/RecordsPanel.tsx:45-52`、`TimelineViewport.tsx:301-303`、`CONTEXT.md:71-72`
- 方向：区分「观测到且为空」与「没观测」，文案分开。
- 复核：静态可判。

### W-07 通用 point 泳道被硬编码播报成「输入密度曲线」（P2）

- 机制：`DensityCurve` 的 aria-label 写死了「输入密度曲线」，可这个组件是通用 point 泳道的渲染器。将来任何非输入类 point 协议都会被念错。
- 证据：`src/components/replay/DensityCurve.tsx:229`、`src/components/records/renderers/registry.ts:44-47`、`TimelineViewport.tsx:302`、`tests/e2e/fixtures.ts:22-31`
- 方向：label 由 Track/协议提供，组件只管画。这条是刚做的密度改造带进来的，趁热改最便宜。
- 复核：静态可判。

### W-08 概览把点观测按查询桶宽画成实心区间块（P2）

- 机制：点观测本身没有时长，概览却按查询桶宽画成实心块，视觉上等于宣称「这一整段都在发生」。桶宽还随范围变，同一份数据在不同缩放下看起来时长不同。
- 证据：`src/components/replay/ActivityOverview.tsx:114-143`、`src/components/replay/timeRange.ts:86-91`、`CONTEXT.md:59-60,71-72`
- 方向：概览里点观测用密度或刻度表达，不要用实心区间。
- 复核：静态可判。

### W-09 渲染期从 QueryClient 读缓存（P2）

- 机制：渲染过程中直接读 QueryClient 缓存，既让整条 memo 链失效，又是非响应式读取——缓存变了不重渲染。
- 证据：`src/api/queries.ts:58-60`、`ReplayWorkbench.tsx:109-119`、`TimelineViewport.tsx:61-74`
- 方向：改成 hook 订阅。
- 复核：静态可判。

### W-10 `ReplayWorkbench` 已经是页面级 God Component（P2）

- 机制：306 行里塞了范围状态、勾选状态、查询编排、缓存读取、布局。而它和 `api/queries.ts` 都没有单测，唯一的证明是浏览器测试。
- 证据：`ReplayWorkbench.tsx:28-149`、`:151-305`、`src/components/replay/*.test.ts`（只覆盖纯模块）
- 方向：把范围与勾选状态、查询编排各自抽成可测的 seam。这条和 `X-A3` 是同一件事的两面。
- 复核：静态可判。

### W-11 后端响应零校验（P2）

- 机制：响应不做任何形状校验，字段缺失或类型变化会一路走进几何计算，最后变成 NaN 路径——屏幕上是空白或畸形曲线，控制台没有错误。
- 证据：`src/api/client.ts:35-49`、`src/api/types.ts`、`src/components/replay/densitySeries.ts:33-40`、`rangeLayout.ts:9-19`、`AGENTS.md:5-9`
- 方向：边界处校验一次，失败就明确报错。
- 复核：静态可判。

### W-12 `trackLabel` 寄生在已废弃的 `TrackPicker` 里（P3）

- 机制：`TrackPicker` 已废弃却还在，只因为一个被四个地方引用的 `trackLabel` 住在里面。
- 证据：`src/components/filters/TrackPicker.tsx:10-12`、`:14-43`、`ReplayWorkbench.tsx:17`、`TimelineViewport.tsx:8`、`TimelineLane.tsx:3`、`DensityCurve.tsx:11`、`src/components/replay/protocolSummary.ts:13-20`、`protocolSummary.test.ts:48-62`
- 方向：`trackLabel` 挪到该在的地方，删掉 `TrackPicker`。
- 复核：静态可判。

### W-13 密度可见性有两份实现（P3）

- 机制：「这条泳道要不要显示密度」在 projection 和 lane 两处各判一次，粒度还不一样。
- 证据：`src/components/replay/timelineProjection.ts:22-33`、`TimelineLane.tsx:110-119`
- 方向：判定归一处。
- 复核：静态可判。

### W-14 组件靠 DOM data 属性通信（P3）

- 机制：组件之间用 data 属性传状态，还有一批属性没有任何消费者，只有测试在读。
- 证据：`TimelineViewport.tsx:104,140`、`TimelineLane.tsx:121-124`、`ActivityOverview.tsx:46-48`、`DensityCurve.tsx:226-228`、`tests/e2e/replay.spec.ts:241-245`
- 方向：状态走 props；测试要的锚点保留但标明用途，无人消费的删掉。
- 复核：静态可判。

### W-15 401 只有详情面板处理（P3）

- 机制：会话过期后只有详情面板会引导重新登录，主页面停在一个自己走不出去的错误态。
- 证据：`RecordsPanel.tsx:15-21`、`ReplayWorkbench.tsx:169-178,241-255`、`src/app/providers.tsx:17-20`、`src/auth/config.ts:46-48`
- 方向：401 提到全局处理。
- 复核：静态可判。

### W-16 一组细节层面的领域语言与兜底偏差（P3）

- 机制：渲染器里的措辞与 `CONTEXT.md` 的词表有出入，兜底分支把未知情形说成确定结论，还有一处样式硬编码。单条都小，攒起来会慢慢污染词表。
- 证据：`src/components/records/renderers/DesktopSystemV1.tsx:10-13`、`:32-34`、`:59-66`、`:92-102`、`registry.ts:40-43`、`RecordValue.tsx:22-45`、`src/app/globals.css:1222`、`CONTEXT.md:36`、`:67-68`
- 方向：和 `W-06`、`W-07` 一起，做一轮词表对账。
- 复核：静态可判。

---

## X 横切：测试证明力、文档一致性、Developer CLI、仓库规范

这一分区最值得看，因为它决定了前面所有条目「以后会不会再犯」。

**本轮处理状态（2026-09-17，工作单 `.scratch/x-series-hardening/`）**：下面的条目描述保持体检当时的原文不改，处理结果只在这里汇总。

| 结果 | 条目 | 落点 |
| --- | --- | --- |
| 已修 | X-A2 / S-04 | `RealAuthenticationPipelineTests.cs`、`TestIdentityProvider.cs`：真实 JWT 自签验签，14 个用例，mutation 验证能红 |
| 已修 | X-A1、X-C2、X-C3、X-C5 | `QualityBase.cs`、`BaselineWorkspace.cs`、`StockBudget.cs`、`SourceMetrics.cs`：不可用基点直接失败并给诊断，`--base anchor --stock` 能度量存量 |
| 已修 | X-B1、X-B2、X-B4、X-B5 | 删掉会腐坏的测试数量快照，确认间隔改成代码当前语义，`development.md` 不实宣称降级，README 索引补齐 |
| 已修 | X-B3、X-A4、X-C4、X-D4 | `docs/verification.md` 口径诚实化（jscpd 确实参与闸门、场景层只有 `native-desktop` 真起进程）、prune 默认能删、契约文档改动会触发对应测试 |
| 已修 | X-D1、X-D2、X-D3、X-C1、M-EX | 清掉完成态 scratch、建起本轮 issue tracker、补 ADR-0009、`docs/verification.md` 加「Developer CLI 的边界」、新增 `docs/agents/closeout.md` 与 AGENTS 的 ADR 门槛 |
| 已缓解 | X-A5、X-A6、X-E2、M-03b | 测试重复改成「只拦新增簇」并已消掉本轮新增 3 簇；标题静置规则由 `tests/window-title-dwell-scenarios.json` 两侧对账钉住 |
| 顺延 | X-A3 / W-10、X-E1 | 记在 `.scratch/x-series-hardening/issues/06-deferred.md` |

存量数字（`quality --base anchor --stock`，收口时）：生产 LOC 11,564 / 测试 8,781 / 工具 4,362；生产重复 14 簇 1.05%，测试重复 15 簇 1.62%；复杂度热点 12（预算 12，最高 17）；erosion 40.11%。**存量本身没降，本轮只是让它第一次被量出来。**

### X-A1 质量闸门几乎总是 `--base HEAD`（P1）

- 机制：38 次质量运行里 37 次 base 是 HEAD。这个基点下闸门只看本次改动，已经提交的债务一律不算。唯一一次跨基点度量（`20260917T041123Z`，base=main）直接失败。也就是说「erosion 40.11%、-3.31%」这类数字，从来没在一个真正的基线上被度量过。
- 证据：`docs/verification.md:18`、`:22,30`、`QualityCommand.cs:118`、`.artifacts/verification/`（38 次中 37 次 base=HEAD）、`20260917T022051Z`/`035332Z`/`035940Z` 的 `quality.json`、`20260917T041123Z-quality-git-delta-…/quality.json`
- 方向：定期跑一次跨基点度量（对 main 或对某个锚点 tag），并把「跨基点必须能跑通」当作工具本身的验收项。
- 复核：已实测——我这轮试过 `--base main`，它确实失败：main 的生产 LOC 被算成 0、复杂度扫描报 `CA1502 produced no production function metrics`、重复检测把新实现全量当成新增。

### X-A2 真实 JWT 验签零覆盖（P1）

- 机制：集成测试整体替换了认证管线，那些 401 断言证明的是测试替身的行为，不是真实验签。`S-02`（audience 静默关闭）这类缺陷因此结构上不可能被现有测试发现。
- 证据：`tests/Heartbeat.Integration.Tests/RecordingApiFactory.cs:38-47`、`:58-72`、`CollectorHttpTests.cs:55-68`、`:134-142`、`RecordUploadHttpTests.cs:304`、`TrackHttpTests.cs:159`、`TrackCatalogHttpTests.cs:83`、`RecordReplayHttpTests.cs:221`、`AuthenticationContractTests.cs:9-40`、`docs/recording-api.md:13`、`AuthenticationExtensions.cs`
- 方向：加一组走真实 handler 的用例（自签 JWKS）。做法可以照 MEMORY 里那次的经验：把 host 的认证配置提取成一个可被测试复用的注册方法，让测试和 host 走同一段代码。
- 复核：静态可判。

### X-A3 前端组合层只有 fixture 化浏览器测试加 CSS class 几何断言（P2）

- 机制：`ReplayWorkbench`(306)、`DensityCurve`(256)、`TimelineLane`(221)、`ActivityOverview`(191)、`api/client.ts`、`api/queries.ts` 都没有单测。浏览器测试跑在 fixture 上，断言又大量落在 CSS class 和几何数值上——它证明的是「渲染出了预期形状」，不是「编排逻辑正确」。
- 证据：`tests/e2e/replay.spec.ts:25`、`:26`、`:67`、`:158`、`:255,323,353`、`:267`、`:269`、`:277`、`:349`、`:432`、`ActivityOverview.tsx:103`、`tests/e2e/fixtures.ts`
- 方向：和 `W-10` 一起做，编排层抽出来补单测。
- 复核：静态可判。

### X-A4 场景验证是第一层测试的子集重跑（P2）

- 机制：`scenario delivery` 与 `replay-fixture` 跑的是第一层测试的子集，所谓「第三层场景证据」没有增加任何新覆盖，但在文档里被当成独立一层。
- 证据：`ScenarioCommand.cs:37-46`、`:48-59`、`VerificationPlanning.cs:125-138`、98 份 manifest 统计、`docs/verification.md:3`
- 方向：要么让场景层真的端到端（真进程、真 Postgres、真认证），要么在文档里降级它的说法。
- 复核：静态可判。

### X-A5 测试重复被量到 63 处但无人消费（P2）

- 机制：jscpd 量出 63 处测试重复，这份报告按设计不进闸门，也没有人看。
- 证据：`.artifacts/verification/20260917T041123Z-…/jscpd-observation/jscpd-report.json`、`RecordUploadHttpTests.cs:84-96`、`:217-230`、`TrackHttpTests.cs:38-52`、`:65-78`、`RecordReplayHttpTests.cs:45-56`、`DesktopRecordProjectorTests.cs`、`TimelineViewport.test.tsx`、`docs/verification.md:37`
- 方向：要么定阈值进闸门，要么明确「测试重复容忍」并从报告里去掉，别留一个没人看的数字。
- 复核：静态可判。

### X-A6 ADR-0006 / ADR-0003 的不变量没有测试（P3）

- 机制：不变量还没实现所以没测试，这本身合理；问题是 `CONTEXT.md` 的领域语言已经先行承诺了这些语义。读文档的人会以为它们成立。
- 证据：`ADR-0006:30`、`recording-open-questions.md:45,55`、`CONTEXT.md:35,63`、`PostgresRecordStore.cs`
- 方向：CONTEXT 里给未实现的语义加显式标注。
- 复核：静态可判。

### X-B1 文档写死的测试数量快照已失真且互相矛盾（P1）

- 机制：四份文档各自写死了一个测试数量，彼此不一致，且都和现在的真实数量不符。这类快照是 main 上 M1（状态漂移）的经典载体。
- 证据：`docs/hub-record-delivery.md:160`、`:163`、`docs/validation/system-capability-inventory.md:33`、`docs/validation/system-acceptance.md:58`、`docs/validation/experience-visualization.md:22`、`replay.spec.ts`/`auth.spec.ts`、`src/**/*.test.ts(x)`、`Heartbeat.Dev.Tests`、`Heartbeat.slnx`
- 方向：删掉所有硬编码数量。要展示就让工具生成。
- 复核：静态可判。

### X-B2 最大确认间隔默认值文档漂移（P2）

- 机制：代码和协议文档是三倍，另有两份文档写两倍。
- 证据：`CollectorOptions.cs:48-49`、`:50-51`、`ADR-0007:16`、`:31`、`ADR-0005:49`、`docs/protocols/desktop-application-foreground-v1.md:47`、`system-capability-inventory.md:19`、`docs/validation/system-acceptance.md:11`
- 方向：以代码为准改文档。ADR 是历史记录，改的话用注记而不是改原文。
- 复核：静态可判。

### X-B3 jscpd 观察报告其实参与闸门（P2）

- 机制：文档说它是观察项不进闸门，实际会让 quality 失败，而失败原因不带诊断信息，看了不知道该改哪。
- 证据：`CloneDetector.cs:51`、`:58`、`:83-86`、`docs/verification.md:37`、`20260917T041123Z` 的 `quality.json`、`DependencyInjection.cs 20-30↔21-31`、`MacKeyPositionMapper.cs 13-112↔14-113`
- 方向：先定它到底是不是闸门，再让失败输出可诊断。
- 复核：已实测（跨基点那次就是被它挂掉的一部分）。

### X-B4 `development.md` 宣称已实测但无证据（P3）

- 机制：文档写「Web 通过真实 API 展示 Record」已实测，98 次 artifacts 里找不到对应场景与证据。
- 证据：`docs/development.md:151`、`docs/verification.md:37`、`:38`
- 方向：补一个真场景，或者把这句话改成待验证。
- 复核：静态可判。

### X-B5 README 文档索引落后于已实现协议（P3）

- 证据：`README.md:16-17`、`docs/protocols/`（5 份）、`DesktopProtocols.cs`、`components/records/renderers/registry.ts`、`docs/validation/`、`docs/agents/`
- 方向：补索引。
- 复核：静态可判。

### X-C1 Developer CLI 已达 4068 行（P2）

- 机制：工具代码是生产代码 11811 行的 34%，职责从「验证」扩到环境编排、探针、数据库导出。它自己没有闸门约束，正在长成第二个系统。
- 证据：`quality.json`（`currentToolingLines=4068` / `currentProductionLines=11811` / `currentTestLines=7802`）、`DeveloperCli.cs`、`ComplexityDetector.cs`(352)、`ProbeCommand.cs`(350)、`EnvironmentCommand.cs`(306)、`SourceMetrics.cs`(230)、`DatabaseReadings.cs`、`development.md:150`、`.scratch/window-title-churn/spec.md`、`Heartbeat.Dev.Tests`
- 方向：给工具划边界——哪些能力属于 CLI，哪些该是一次性脚本用完删。探针类（`WindowTitleChurn`）尤其像一次性。
- 复核：静态可判。

### X-C2 复杂度闸门只拦增量（P2）

- 机制：复杂度只拦新增回归，Erosion、coupling、LOC 全是观察项。于是 12 个热点可以长期驻留，指标只在图上飘。与 `X-A1` 叠加后，效果是「闸门只保证今天没变差，不保证已经不坏」。
- 证据：`ComplexityDetector.cs:270`、`docs/verification.md:38`、`:39`、`:49`、artifacts 里 38 次 quality 的趋势
- 方向：给热点定收敛计划或绝对阈值。
- 复核：静态可判。

### X-C3 基线物化依赖临时 worktree 加 `npm ci`（P2）

- 机制：跨基点度量要临时建 worktree 并 `npm ci`，慢且脆；历史失败里工具自身失败占大半。这也解释了为什么大家都退回 `--base HEAD`（`X-A1`）——不是不想量，是量不动。
- 证据：`ComplexityDetector.cs:42-53`、`:170-172`、`:323`、`20260915T053052Z`、`053150Z`、`062759Z`、`070214Z`、`070536Z`、`041123Z`
- 方向：基线产物缓存复用，别每次重装依赖。顺带一句经验：worktree 路径要建在自己控制的目录下，别放在会被别的工具清理的地方。
- 复核：已实测。

### X-C4 `artifacts prune` 默认参数基本是空操作（P3）

- 机制：默认阈值在当前节奏下永远命中不到，于是证据目录堆到 2.7G、`.local` 12G。
- 证据：`ArtifactsCommand.cs:127-128`、`ArtifactStore.cs:62-69`、`.artifacts/verification`（98 run）、`docs/verification.md:105-107`
- 方向：改默认值，或者跑完自动 prune。
- 复核：已实测（磁盘数字是量出来的）。

### X-C5 `SourceMetrics` 兜底分类不准（P3）

- 机制：`src` 之外一切归为 tooling，Dockerfile 与 MSBuild 计入生产 LOC。所有 LOC 与 erosion 数字都带这层误差。
- 证据：`SourceMetrics.cs`、最新 `quality.json` 的 `productionLanguages`、`docs/verification.md:36`
- 方向：分类规则显式化。
- 复核：静态可判。

### X-D1 已完成的 `.scratch` 没删（P2）

- 机制：`AGENTS.md:21` 要求做完就删，`window-title-churn/spec.md` 还在，另有两个空目录残留。
- 证据：`AGENTS.md:21`、`.scratch/window-title-churn/spec.md:3`、`git ls-files .scratch`、`.scratch/frontend-timeline-status`、`.scratch/verification-foundation`、`.scratch/verification-foundation/issues`
- 方向：删。顺便说，这份清单本身也放在 `.scratch/`，过完之后同样要删。
- 复核：静态可判。

### X-D2 issue tracker 与 triage labels 零使用（P2）

- 机制：两份流程文档立在那里，一次没用过，`issues` 目录是空的。要么它不适配你现在的节奏，要么它就该删。
- 证据：`docs/agents/issue-tracker.md`、`docs/agents/triage-labels.md:7-11`、`.scratch/verification-foundation/issues`（空）
- 方向：这份候选清单其实是个天然的试用机会——你要是想用，我就把选中的条目按那套约定落成 issue。
- 复核：静态可判。

### X-D3 Hub 持久接管没有 ADR（P2）

- 机制：`2d86169` 让 Hub 接管注册与持久投递，这是架构级决定，只留了一份 153 行的说明文档，没有 ADR；而「窗口标题必须静置」这种小得多的决定有 ADR-0008。ADR 的门槛不一致，以后回溯会踩空。
- 证据：提交 `2d86169`、`docs/hub-record-delivery.md`（153 行）、`docs/adr/`（8 份）、`ADR-0005:13,25,47,53`、`ADR-0006:37`、`AGENTS.md:14`
- 方向：补一份 ADR，并把「什么级别的决定需要 ADR」写清。
- 复核：静态可判。

### X-D4 验证契约文档改动不触发任何检查（P3）

- 证据：`VerificationPlanning.cs:100-101`、`:108`、`VerificationReporter`(`:188`)、`docs/verification.md`、`docs/protocols/*`、`docs/recording-api.md`
- 方向：契约类文档进 changed-files 触发规则。
- 复核：静态可判。

### X-E1 生产代码已有 7 处重复簇（P3）

- 机制：最典型的是两个 macOS native 观察器里约 29 行几乎相同的桥接代码，另有 Application 层与 Infrastructure 层的成对重复。
- 证据：`jscpd/jscpd-report.json`、`MacAccessibilityNative.cs:99-110↔MacInputMonitoringNative.cs:73-84`、`:110-126↔:84-100`、`:490-502↔:278-290`、`CountPointRecords.cs:29-66↔ReplayRecords.cs:42-82`、`:55-68↔ReplayRecords.cs:70-84`、`PostgresPointRecordCountStore.cs:11-26↔PostgresRecordReplayStore.cs:10-27`、`RecordEndpoints.cs:202-210↔TrackEndpoints.cs:73-81`、`:208-225↔:111-128`
- 方向：native 桥接抽共享 helper。抽取的时候注意核对被 catch 的异常类型集合，这是上一轮踩过的坑。
- 复核：静态可判。

### X-E2 债务全靠文档承载的结构性风险（P3）

- 机制：`src`/`tests`/`tools`/`docs` 里 `TODO|FIXME|HACK|XXX|for now|workaround` 零命中——干净得可疑。真相是所有已知缺口都被写进了文档（`recording-open-questions.md` 等约 1928 行）。好处是集中，坏处是代码现场没有任何提示，改到那一行的人不会知道有个 open question 挂着。
- 证据：`docs`+`.agents` 约 1928 行、`recording-api.md`(273)、`recording-storage-model.md`(269)、`recording-open-questions.md`、`docs/validation/*`
- 方向：关键缺口在代码现场留一行指向文档的锚，别只在文档里说。
- 复核：静态可判。

---

## M 与 main 的纵向对照

从 `main@86911e7` 提炼出九个腐坏模式，逐条对新实现体检。

| 模式 | main 上的样子 | 新实现结论 |
|---|---|---|
| M1 状态漂移：文档、issue、验收勾选与实现不一致 | `engineering-friction.md:6`、`.scratch/observation-convergence/PRD.md` | **有苗头，但是声明式的**：有「已接受但未实现」的决策，好在都原地标注了并进了 open questions。真正的漂移在 `X-B1`/`X-B2` |
| M2 兼容债务扩散、没有退出条件 | `engineering-friction.md:8`、`:19-20`、`docs/architecture/compatibility-debt.md` | **已复发 1 处**，见 `C-13`。只在工具层，现在删最便宜 |
| M3 同一契约多个可漂移权威 | `ObservationValidation.cs`、`FactAspects.cs`、`ObservationCompatibility.cs`、Browser 的 `protocol.ts` | **已复发，本轮最重**：见 `S-07`（五处映射）、`C-14`（四处 type 字符串），另有 `M-03b` |
| M3b 生产规则与探针规则各一份 | — | **有苗头**：`DesktopRecordProjector.cs:101-161` 与 `tools/…/WindowTitleChurn.cs:138-176` 各写一份标题静置规则 |
| M4 通用层认识具名可选组件 | `ObservationValidation.cs`、`JsonCollectorRuntimeStore.cs` | **已避开**：`src/Backend`、`src/Hub` 里 `git grep "desktop\."` 零命中 |
| M5 「代码完成」当成「需求完成」 | `engineering-friction.md:10`、`:24`、`ORCHESTRATION.md` | **已避开且已制度化**：`docs/verification.md` 加 verify skill |
| M6 运行时 God File | `CollectorRuntime.ManagedProcess.cs` 2147 行、`client.ts` 约 414KB | **已避开**：最大 `MacAccessibilityNative.cs` 510 行。但 `W-10`、`X-C1` 是同方向的早期信号 |
| M7 ADR 连环 supersede | 59 份，001→018、007→013→058、045→048… | **已避开**：8 份无 supersede。机制已埋下（`ADR-0002:17`），门槛不一致见 `X-D3` |
| M8 为局部噪声引入跨子系统耦合 | `ADR-016` 的 `IInputActivitySignal.LastClickTicks` + 逐 App formatter | **已避开，而且是明确的反向决策**：`ADR-0008` |
| M9 协调材料淹没代码 | 266 篇 md（`.scratch/` 144）、86 个 migration | **已避开**：4 份 `.scratch/*/spec.md` 已删。残留见 `X-D1` |

### M-EX 你自己写的病历没带进新仓（P2）

- 机制：main 的 `docs/agents/` 有 5 份，其中 `engineering-friction.md`、`dotnet-refactoring.md` 没进新仓；新仓只带了 domain、issue-tracker、triage-labels。那两份恰好是记录 M1、M2、M3 怎么发生的病历。缺了它们，新仓里就没有任何显式的收口检查项去拦这几条模式复发——而 M2、M3 已经复发了。
- 证据：`main:docs/agents/`（5 份）、`cleanroom-rewrite:docs/agents/`（3 份）、`docs/verification.md`
- 方向：把病历里那几条模式提炼成一张收口检查清单（比如「新增兼容分支必须写退出条件」「同一契约新增第二处映射必须给对账」），挂进 `AGENTS.md` 或 verify skill。这比事后体检便宜得多。
- 复核：静态可判。

### 21 个提交里的返工痕迹

不是问题，是判断「领域收敛没收敛」的材料。采集语义被连续推翻过多次，说明这块还在动，现在给它上重构不划算：

- `2d86169` 推翻早期的客户端注册与投递归属（删掉 `HeartbeatRecordingClient.cs`、`RecordProtocol.cs`、`RecordProtocols.cs`、`IContinuousStateStore.cs`，它们来自 `3ca4aed`/`7ba1aa7`）
- `bf2169b` 整体替换 macOS 采集三件套（删 `ForegroundRecordBatcher.cs`、`MacForegroundApplicationReader.cs`、`PendingForegroundRecords.cs`）
- 标题静置这条线：`ADR-0005` → `c2b6d69` → `ADR-0008` → `a0cfc23`
- 前端时间线：`7871897` → `85a4973` → `9f7467d` → `27cdf6f`
- 工具链：`3954338` → `d269b7d` → `c4a5b44`
- 高频触达文件：`Heartbeat.Infrastructure/DependencyInjection.cs` 改过 9 次、`docs/recording-storage-model.md` 9 次、`README.md` 9 次、`Persistence/README.md` 8 次

---

## 我建议的过审顺序

一次看一条，按这个顺序性价比最高：

1. **先堵数据丢失**：`C-01` → `C-04` → `C-02`（连 `S-11`）→ `C-03`。这四条决定「记下来的东西还在不在」，其他都是其次。
2. **再修用户看到的错**：`W-01` → `W-02` → `W-06`/`W-07`（领域语言，趁密度改造还热）。
3. **然后是安全边界**：`S-02` → `S-03` → `X-A2`/`S-04`（补真实验签测试，一起做）。
4. **接着是把闸门修成真闸门**：`X-A1` → `X-C3` → `X-B1`。这一组做完，后面所有条目才有「不会再犯」的保证。
5. **最后收腐坏苗头**：`C-13`/`M-02` → `S-07`/`C-14`/`M-03` → `M-EX` → `W-10`/`X-C1`。

## 明确没查的、以及要留神的

- 全部结论是静态阅读加已有 artifacts 得出的。本轮**没有跑** `dotnet test`、`npm run verify`、`docker compose up`，也没有真起 Collector 长跑。标了「需实测」的条目，结论强度低于标「静态可判」的。
- `--base main` 的跨基点质量度量本轮试过一次，失败了（`X-A1`）。所以 erosion、clone、complexity 这几个数字**都只在 `--base HEAD` 语义下成立**，别当成绝对水位。
- 严重度是我按「值不值得现在停下来看」给的，不是故障等级，你不同意就直接改。
- 这份文件放在 `.scratch/`，按 `AGENTS.md:21` 过完就该删。
