# 采集器即插即用运行时

Status: ready-for-agent

## 构想

未来让 Collector 能够独立发现、安装、组合、启用、更新、回滚和移除，而不必为了增减采集能力重新构建 Agent。

版本管理可参考 pnpm、NuGet 等包管理器：不可变的包版本、带兼容约束的清单、版本范围解析、精确锁定状态，以及下载后校验、切换和回滚。这里借鉴的是依赖图、版本范围、内容校验、锁定和回滚等机制，不预设沿用它们的仓库形态或命令行体验。

用户提供的 Cordis 论文及 DeepSeek Harness 开源实现已经完成评估；论文提供生命周期所有权与依赖驱动组合的参考，但不替代 Heartbeat 的跨进程统一协议与事实模型。设计 grilling 已结束，本文保留其决策背景；当前实现状态需要按 issue 10 与两份规范完成对账。

## 本轮收口目标

本轮只固定两块会被 Collector 数量放大的公共地基：

1. **统一 Collector Runtime / Protocol**：Package、Instance、Activation、Desired/Actual、能力协商和 Fact 交付不再因 InProcess、ManagedProcess、ExternalHost 各长一套。
2. **统一观测事实模型**：Subject 与 Hub 解耦，Segment、Event、Measurement 共享最小信封但保留各自时间与收敛语义。

代码实现与自动验证已经完成，但需求还不能标为 `done`：issue 08 仍有本地真实 VRChat 账号
smoke，issue 10 仍需把 PRD / Protocol / Fact 文档与已交付实现对账。因此本 PRD 当前是
`ready-for-agent`；issue 10 完成后再转 `ready-for-human` 承接真实账号门禁。

2026-08-28 closeout audit：完整 .NET、Dashboard、Browser 与 Fact contract 回归通过；恢复数据的
baseline→新 Desktop 数据 verify 通过。审计同时发现本 PRD 的“实现前矛盾”、覆盖表和两份
`Draft 0.2` 未随实现收口，已建 issue 10；真实 VRChat 账号 smoke 尚未执行。

包市场、安全体系、完整包管理体验、磁盘保留策略与每个故障分支都不是本轮前置条件。已经讨论出的合理方向可以保留在 PRD，除非它影响上述两份公共契约，否则不再单独建 ADR，也不继续用 grilling 展开。

## 已确认的设计边界

2026-08-17 至 2026-08-22 grilling session 已确认以下边界（ADR-040、ADR-041）。这里只保留决策本身；消息形状、字段与校验规则不在本文复制，权威定义在 [Collector Protocol v1](./spec/collector-protocol-v1.md) 与 [Fact Model v1](./spec/fact-model-v1.md)。

### 身份与分层

- Collector Package、Collector Instance、Collector Activation 与 Source 是四种不同身份；Source 只表达观测者。
- Collector Package 的分发/安装运行时与 Collector 内部可能采用的细粒度组件运行时分层，不以“一切皆组件”重写 Hub 内部。
- Collector 只依赖宿主协议与能力，不提供或消费跨 Collector 运行时服务；每个包携带私有依赖。
- Hub Instance 是运维宿主而不是观测主体；一个无头 Hub 可以持续托管观测多个账号、身体或其他主体的 Collector Instance。
- Owner 拥有 Machine、Account、Person 等 Subject；每个 Collector Instance 观察一个 Subject，一个 Subject 可以由多个 Instance 观察，Hub Instance 不参与事实归属。
- Activation 身份与 Package 身份必须分开建模；credential、签名与配对机制留到安全阶段实现。

### 运行时与生命周期

- 首版目标是不重新构建或发布 Agent 即可安装、切换和回滚 Collector；允许重启宿主完成切换，不承诺无停机热卸载。
- system、browser、vrchat.account 都进入同一 Package / Instance / Activation / Runtime State 模型，由 Artifact Delivery 与 Execution Driver 两轴表达不同控制能力。
- 制品交付与执行控制分成正交两轴；旁加载 browser 可以由 Runtime 交付制品，但其执行仍由浏览器控制。
- vrchat.account 当前只是已验证采集能力的终端原型；产品化后作为无头 Hub 同机的托管进程，经本机协议向 Hub 发送数据，不再采用 ADR-032 原定的进程内 BackgroundService 直推。
- 更新事务以单个 Collector Instance 为边界；宿主升级单独对全部实例执行兼容性预检。
- 首版 Collector Desired State 由各 Hub 本地持有，但必须是独立、可同步的表达；Heartbeat Server 暂不作为远程控制面。
- Collector Desired State 每次有效变更递增 SpecRevision；Activation 明确报告 AppliedSpecRevision，动态配置能力缺失时由 Runtime 创建新 Activation 完成收敛。
- Activation 显式经过 Starting、Authenticating、Negotiating、OpeningStreams、Ready、Draining、Stopped，并可进入 Failed、Degraded 或 WaitingForExternalHost；更新只在候选取得所需 Stream writer lease 并到达 Ready 后提交，不等待第一条 Fact。

### 包、清单与配置

- 同一 PackageId + Version 使用一份 Manifest，内部列出多个按 OS、CPU 架构和 Execution Driver 选择的 Artifact Variant；每个变体独立记录入口点、大小与内容哈希。
- Package manifest 静态声明 Output Template；Activation 只能把模板绑定到具体 Subject 与合法 dimension values 形成 Fact Stream。
- Instance Config 归属于稳定 Collector Instance，以带 ConfigVersion 的 JSON 保存；候选包不能接受当前版本时阻止更新，首期不运行包提供的自动迁移代码。

### 协议与版本协商

- Collector Protocol 是 transport-neutral 的语义消息与状态机；InProcess、ManagedProcess、ExternalHost 使用不同 Transport Binding，但共享 canonical JSON 与契约测试。
- 基础协议交换支持的 Major 集合并选择最高交集；Fact、配置和生命周期能力独立版本化，Package SemVer 不参与 wire negotiation。

### 事实模型

- Collector Protocol 与包清单承认 Segment、Event、Measurement 三类输出能力；首期可以只实现已有管道，不能把公共协议固化为 `/v1/segments` 的别名。
- 三类事实共享最小信封，但使用类型化 payload 与各自的收敛规则：Segment 支持单调 Revision，Event 默认不可变，Measurement 以 Stream 与点/窗口身份去重并显式修正。
- Measurement 从协议首版定义 Gauge、Sum、Histogram 以及时间窗、temporality、monotonic、unit、missing/reset 等语义；实现可以通过能力协商只支持子集。
- Measurement Stream 的 identifying dimensions 必须由声明给出且保持低基数；ActivationId 只作 provenance，同一 Stream 同时只允许一个逻辑 writer。
- Fact Stream 持有 Subject、Collector Instance、Source、FactKind、Schema 与 identifying dimensions；逐 Fact 只携带 StreamId、FactId、Revision、事实时间、可选 ObservedAt 与类型化 payload，Hub 自行记录 ReceivedAt。
- Hub 为 Output Template 的具体绑定分配 opaque StreamId；它跨 Activation 与兼容 Package 更新稳定，Subject、outputId、Schema Major、Measurement descriptor 或 identifying dimensions 改变时新建 Stream。
- Collector 为 Fact 首次 occurrence 生成 UUIDv7 FactId，重试、重新分批和更高 Revision 保持不变。
- 持续中的 Segment 发送同一 FactId、递增 Revision 的完整 snapshot；pulse/extend 只作 SDK 便利，不成为 Hub 按 payload 相等猜测合并的 wire 语义。
- Fact Schema 以 SchemaId + SchemaMajor 表达兼容线，以 SchemaRevision + SchemaHash 锁定同 Major 内只增可选内容的精确定义；每条 Fact 携带其实际 SchemaRevision，破坏性变化创建新 Stream。

### 交付、确认与背压

- Collector 到 Hub 使用至少一次交付：Collector 在 ACK 前保留并重试，Hub 取得持久化责任后才 ACK，重复由 StreamId + FactId + Revision 幂等收敛。
- 协议协商批大小与最大在途批次并显式背压；Collector 无法避免的数据丢失必须用 Stream Gap 协议诊断披露，不能静默丢弃，也不增加第四种业务 Fact。

### 安全（本期只固定结构，不实现）

- 目标安全策略是正式模式只运行 Heartbeat 官方包、本机开发模式显式允许开发包；签名、信任根与执行隔离不属于本期实现。
- Artifact Delivery、Package Verification 与 Activation Package Match 分开：交付类型不自动决定可信结果，验证状态由证据与策略派生，不能变成人工可信度评分。
- 上述验证模型只是未来安全阶段的结构边界；本期不实现 Trust Policy、签名根、轮换、撤回或 Activation 认证。

这些细节足以起草统一协议与 Fact schema。卸载与回滚保留、崩溃重启、长期离线和各层数据保留边界进入后续实现议题，不再阻塞本轮收口。

## 术语

产品文案和正式模型统一使用 **Collector**；“插件”只作为未来机制的非正式称呼。

以下版本轴彼此独立：

| 版本轴 | 含义 | 示例用途 |
| --- | --- | --- |
| Collector 发布版本 | 某个 Collector 包/产品的 SemVer 发布版本 | 安装、升级、回滚、安全修复 |
| Collector 协议版本 | Collector 与 Agent/本机宿主之间的协议版本 | 回环上报、配置、声明、生命周期协商 |
| Agent 发布版本 | Windows、macOS 或无界面 Agent 的产品发布版本 | 客户端升级、问题定位、灰度分组 |
| Agent 协议版本 | Agent 与 Analytics 之间的上报协议版本 | 服务端兼容协商；当前由 `X-Heartbeat-Protocol-Version` 表达 |
| Collector 声明版本 | 现有 Observation Depth 声明本身的版本 | 判断采集能力声明是否变化 |

Collector 声明版本不等于 Collector 发布版本，也不等于 Collector 协议版本；这些版本不要求同步递增。一个发布版本可以支持多个协议版本，多个发布版本也可以继续使用同一协议版本。

其中 **Collector 声明版本**（ADR-030 Observation Depth 声明）在两份草案里都还没有落点：Manifest 只声明 Output Template，Fact 信封里也没有 `appName / title / identityKey` 这些声明谓词赖以取值的槽位。这是本轮必须裁决的第 3 条矛盾，见[草案暴露的矛盾](#草案暴露的矛盾待裁决)。

## 产品边界

- BuiltIn Collector 可以进程内执行；RuntimeManaged Collector 默认以独立托管进程执行；browser 等 ExternalHost Collector 由外部宿主执行。
- 发现、安装、启用、热替换、卸载、隔离、版本解析和 App 覆盖度是不同问题，分别定义语义。
- 后续设计必须显式兼容 ADR-017 的回环 Collector 拓扑和 ADR-037 的可执行文件/程序集边界。
- “可下载”不等于“可启用”；安装前和启用前都必须进行兼容性检查。
- 同一 `packageId + version` 的制品必须不可变；内容不同必须发布新版本。
- 用户的启用意图与已安装、兼容、已授权、正在运行等事实分开保存，避免暂时失败导致配置丢失。

## 包与清单模型

每个 Collector 包携带一份机器可读 Manifest。字段与示例只在 [Collector Protocol v1 §10](./spec/collector-protocol-v1.md#10-manifest-的协议相关最小面) 维护；PRD 不复制第二份 JSON shape。

约束原则：

- Collector 声明自己支持的基础协议 Major 集合和独立能力版本。
- 只有当宿主能力依赖无法由协议范围表达时，才使用 `requires.agent` 限制 Agent 发布版本。
- Agent 清单声明自己支持的 Collector 协议范围，以及 Agent 与 Analytics 之间可用的协议范围。
- Analytics 元数据声明 Agent 协议的接受、弃用和拒绝范围。
- 兼容解析器比较版本范围和能力需求，不做简单的版本相等判断。
- 同一 Package 版本的 Artifact Variant 共享发布身份；Runtime 按 OS、CPU 架构与 Execution Driver 确定性选择唯一兼容项，零个或多个匹配都返回结构化冲突。
- Manifest 为每个 Artifact Variant 固定入口点、大小与内容哈希，并声明当前配置版本和可接受的旧版本。
- 后续清单还需要表达磁盘需求；权限与信任字段只保留未来扩展位置。

## Package Registry、解析器与锁定状态

为避免与现有 Collector Registry 以及 Windows Registry 混淆，未来的包索引暂称 **Package Registry（包仓库）**。

Package Registry 保存可用包版本、清单、制品地址与内容哈希；签名和撤回元数据属于后续安全阶段。兼容解析器至少接收以下输入：

- Agent 发布版本及其两侧支持的协议范围；
- Analytics 当前接受的 Agent 协议范围；
- 平台、CPU 架构、权限和能力条件；
- 用户请求的 Collector 包；
- 目标 Collector Instance 的当前安装与候选包。

首版 Collector Package 必须自包含私有依赖，不在 Collector 之间解析共享依赖图。解析结果是目标 Collector Instance 的一个精确且兼容的包，或者一个结构化冲突；宿主升级时再对全部 Instance 的结果做集合级兼容性预检。冲突应指出相关包、当前版本、所需范围、宿主支持范围、可行升级候选以及被阻止的动作。

本地锁定状态至少包含：

- 精确的 Agent 发布版本和实际协商出的 Agent 协议版本；
- 精确的 Collector 发布版本和实际使用的 Collector 协议版本；
- 已选择 Artifact Variant 的 ID、哈希与来源；未来可以附加验证证据；
- 每个 Collector Instance 上一个可回滚的已知良好包。

本地锁定状态是安装事实。现有 Collector Registry 继续负责 Source 发现、启用意图、刷新状态和声明缓存，不能为了保存包状态而丢失独立的包身份。

## 实例配置与期望状态收敛

Instance Config 是 Collector Instance 的持久意图，不随 Package 更新或 Activation 重启更换归属。配置保存为 JSON，并与创建它的 ConfigVersion 一起保留；Manifest 声明当前写出版本与候选包能够接受的配置版本。首期由官方 Collector 强类型校验配置，不虚构尚不存在的 Config Schema 文档或哈希。若候选包不能接受当前配置，解析器阻止更新并报告配置冲突，不尝试执行包内迁移代码，也不改写 Desired State。

Collector Desired State 每次有效修改都递增该 Instance 的 SpecRevision。Activation 在握手后取得完整规格快照，并报告 AppliedSpecRevision。若双方协商了动态配置能力，Runtime 可以把新 Revision 原地交给 Activation，并在明确 ACK 后更新实际状态；不支持、拒绝或超时时，Runtime 通过创建新 Activation 使实际状态收敛。Transport 可以采用推送或轮询，但不能改变 revision、完整快照与 ACK 的语义。

## 协议协商与演进

- 协议升级采用“新旧双支持 → 迁移 → 明确弃用窗口 → 移除”的过程。
- 通信双方声明基础协议 Major 集合，并选择最高共同 Major；没有交集时返回结构化拒绝原因。
- Fact、配置、生命周期等能力独立携带版本；新增 Measurement Histogram 等能力不提升整个基础协议 Major。
- 当前 Agent 与 Heartbeat Server 之间精确匹配协议版本 `3` 是另一条上行协议轴，不与 Collector Protocol 共用版本。
- 迁移期间，一个 Agent 可以同时支持多个 Collector 协议代际。
- 每个受支持协议组合都要有契约测试，避免“版本号兼容但行为不兼容”。
- 修复应发布新的包版本，不应为了普通实现修复而无意义地提升协议版本；安全撤回策略后续另定。

## 安装与更新事务

一次安装或更新以单个 Collector Instance 为事务边界：

1. 解析该 Instance 的精确兼容包。
2. 下载候选制品，在修改运行状态前验证内容哈希和平台匹配；签名验证后续加入同一门禁位置。
3. 停止旧 Activation，并保留上一个已知良好包。
4. 用候选包创建新 Activation，完成协议握手和健康检查。
5. 成功后提交该 Instance 的新解析状态；失败则恢复该 Instance 的旧包与 Activation，其他 Collector 不受影响。

Agent 或 Hub 自身升级前必须检查全部 Collector Instance 的兼容性。若需要，解析器应给出各 Instance 可共同兼容的候选；没有兼容结果时阻止宿主升级并说明原因。

是否支持不重启热替换仍待设计。即使首版只支持重启切换，也应保留相同的事务和回滚语义。

## Fact 交付、确认与背压

Collector 到 Hub 的边界采用至少一次交付。Collector 在收到 ACK 前持有并重试未确认 Fact；Hub 只有在把这批 Fact 纳入自己可恢复的持久化责任后才返回 ACK。批次只是传输和确认单位，不参与业务去重；重试、重新分批和进程恢复后的重复由 StreamId、FactId 与 Revision 收敛。此处不承诺 Fact 从 Collector 到 Analytics 的端到端 exactly-once。

握手协商单批最大字节数、最大 Fact 数与最大在途批次数。Hub 可以暂停 credit 或返回显式重试信息施加背压，Collector 不得把“连接仍然存在”当作继续无限发送的许可。Collector 的本地缓冲仍然必须有界；若缓冲耗尽或上游本身造成不可恢复丢失，Collector 在恢复后发送 Stream Gap，至少说明 Stream、缺口事实时间范围、原因和能够得到的估算丢失量。Stream Gap 是协议诊断，不是 Segment、Event、Measurement 之外的新业务事实。

## 服务端记录与可观测性

Heartbeat Server 应知道各 Hub Instance 当前实际运行的版本与 Collector 清单。Hub Instance 是运维身份，不能再把这份清单挂在它所观察的某个 Device/Subject 上；第一阶段保存“当前快照”，不急于保存完整历史：

- Hub Instance 记录最后确认的宿主发布版本、协商出的上行协议版本、平台、CPU 架构和确认时间。
- Hub 定期报告已安装且实际启用的 Collector 清单，包括 `packageId`、Collector Instance、Source、Collector 发布版本、实际使用的 Collector 协议版本和制品哈希。
- 常规心跳可以只携带清单摘要；摘要变化时再上传完整清单，避免每次重复发送。
- 服务端应能查询旧版 Agent、即将退役协议和按平台聚类的失败；漏洞与撤回查询后续补充。
- Dashboard 可以展示版本事实，但 Analytics 不能仅凭观察到版本就直接远程安装包。
- 协议拒绝、版本冲突和自动回滚应形成诊断事件；安全撤回事件与历史保留期限后续再定。

服务端快照是设备上报的运行事实，不用于反向重建或替代本地锁定状态。

## 必须覆盖的兼容场景

1. Agent 4.2 支持 Collector 协议 `>=2 <4`，Collector 1.8 要求 `^2`：协商为协议 2。
2. Collector 2.0 要求协议 4，而当前 Agent 最高只支持 3：拒绝启用，并提示需要升级 Agent。
3. 候选 Agent 不再支持协议 2，但已安装 Collector 只支持协议 2：先寻找可共同升级的 Collector，否则阻止 Agent 升级。
4. Analytics 接受 Agent 协议 `>=3 <5`，Agent 支持 `>=2 <4`：协商为协议 3；协议 3 被移除前必须经过弃用窗口。
5. 仓库返回同一发布版本但哈希不同的制品：判定为仓库完整性错误。
6. 协议握手成功但系统权限缺失：保留启用意图，将运行状态标记为暂停，而不是卸载。
7. Package Registry 离线：已锁定的本地集合继续运行，安装和更新返回明确的离线结果。
8. 候选包不能接受 Instance 当前 ConfigVersion：保留 Desired State 和旧 Activation，阻止更新并返回配置冲突。
9. ACK 丢失后 Collector 重发相同 Fact，但采用不同批次边界：Hub 以 Fact 身份幂等收敛，不能产生重复业务事实。
10. Hub 长时间背压导致 Collector 缓冲耗尽：Collector 明确报告 Stream Gap，系统不能把缺口误判为“期间没有活动”。

这十个场景与 [Collector Protocol v1 §13 契约测试](./spec/collector-protocol-v1.md#13-v1-契约测试最小集合) 是两份独立维护的清单，对照后覆盖情况如下。表里的“Runtime 侧”表示该场景不由 wire protocol 承担，需要在 Collector Runtime 的测试里单独立项，不能默认它已被协议测试覆盖。

| 场景 | 归属 | 当前覆盖 |
| --- | --- | --- |
| 1 有交集时选最高 | 协议 | 缺，§13.4 只测无交集 |
| 2 Collector 要求过高协议 | 协议 | §13.4 |
| 3 宿主升级被已装 Collector 阻断 | Runtime 侧 | 缺 |
| 4 Agent ↔ Analytics 协商与弃用窗口 | 另一协议（ADR-035） | 不在本规范 |
| 5 同版本不同哈希 | Runtime 侧 | 缺 |
| 6 权限缺失保留启用意图 | Runtime 侧 | 缺 |
| 7 Registry 离线 | Runtime 侧 | 缺 |
| 8 ConfigVersion 不被候选包接受 | Runtime 侧 | 缺 |
| 9 换批次边界重发同一 Fact | 协议 | §13.8、§13.9 |
| 10 缓冲耗尽后报告 Stream Gap | 协议 | 缺，§13 无 `stream.gap` 用例 |

## 首阶段建议

1. Agent 能读取并上报自己的发布版本、Agent 协议版本、平台和 CPU 架构。
2. Analytics 在 Device 上保存当前 Agent 版本快照。
3. Collector 配置和声明分别携带 Collector 发布版本与 Collector 协议版本，不复用声明版本。
4. 定义最小包清单和本地锁定格式，首期只做兼容性验证，不急于实现在线安装。
5. 增加版本范围交集、无交集、弃用和升级阻断的契约测试。

这样可以先获得版本可见性和兼容性约束，而无需提前决定进程内或进程外实现。

## 明确不在当前阶段解决

- 默认使用中心仓库、私有仓库还是本地仓库；
- 是否接受未签名的第三方 Collector；
- Collector 内部的细粒度组件热卸载运行时；
- 无重启热更新；
- 服务端远程强制安装的控制面；
- Package 签名、TUF/Sigstore、信任根、密钥轮换、撤回和验证策略；
- Activation 认证、Secret 管理、权限执行、沙箱和资源配额；
- 用版本兼容替代未来的权限、信任、沙箱和资源配额；
- Instance 删除、Artifact GC 与 Last-Known-Good 的完整产品语义；
- 长期离线时的磁盘配额、淘汰顺序与本地 Journal 物理设计；
- 每种进程崩溃、Hub 重启和 Server ACK 丢失的恢复状态机。

## 后续实现议题

- Collector Package 的磁盘需求和覆盖完整度如何声明？
- 谁负责协议弃用决策，长期离线设备跨多个版本升级时如何处理？
- 外部宿主管理的 Collector 如何报告版本与运行身份，同时把自报信息和 Runtime 持有的安装事实分开？认证与证明机制留到安全阶段。
- Segment 的 Revision 如何取代现有 attributes 后写胜，使乱序旧快照不能覆盖新内容？
- Measurement 的迟到窗口、历史修正与派生缓存失效如何按 Source 定义？

## 草案暴露的矛盾（待裁决）

“下一触发点”原本约定：schema 草案暴露真实矛盾之前不再扩张 grilling。草案写完后确实暴露了下面这些，它们不是措辞问题，是两处已确认边界互相打架、或者某个决策在两份草案里都没有落点。每条只描述冲突和需要裁决的内容，不预设答案。

1. **Stream writer lease 的移交与更新回滚互斥。** 本文规定“更新只在候选取得所需 Stream writer lease 并到达 Ready 后提交”，而 [协议 §5.5](./spec/collector-protocol-v1.md) 规定同一 Stream 只有一个 writer、旧 Activation 发布即 `stream_writer_conflict`。候选一旦拿到 lease，旧 Activation 就已经写不进去；此时候选健康检查失败，回滚回去的旧 Activation 也发不出 Fact。需要定的是：lease 是抢占式移交、显式撤销式移交，还是失败时由 Hub 归还给旧 writer。
2. **capability 无交集是致命还是降级。** 协议 §3.2 说“未被选择的能力不得在本次 Activation 使用”，读起来是降级；但错误码表里有 `capability_no_common_version`，读起来是致命。合理的切分大概是 `facts.*`（Output 依赖）无交集则 Activation 失败，`config.dynamic` / `diagnostics.stream-gap` 无交集则降级，但这条规则现在没写在任何地方。
3. **ADR-030 的 Observation Depth 声明在新模型里没有落点。** 协议 §12 把 declaration 说成“Package Output Template 的临时来源”，但两者不是一回事：Output Template 描述的是 source / factKind / schema / dimensions，声明描述的是深度树与读数谓词，且谓词的 `from` 槽位（`appName`、`title`、`identityKey`、`attributes.<path>`）在 Fact 信封里已经全部消失，payload 改由各 schema 自定。Matcher（ADR-029/030/031）直接建在这套槽位上。需要定：声明是随 Manifest 一起静态声明的第四类内容，还是从 Fact Schema 派生。
4. **Fact Schema 文档本身没有格式。** [Fact Model §8](./spec/fact-model-v1.md) 只定义了 schema 的*引用*（id / major / revision / hash），但 §4.1 与 §6.1 已经在引用 schema 级别的 `mutable: true` 和 `allowRetraction: true`，`hash` 也需要一份 canonical bytes 的定义才可验证。schema 用什么 JSON Schema 方言、这两个标志写在哪里、hash 怎么算，目前无处可查。
5. **AppHint 解析与“Hub 不改写 Fact”冲突。** 现有 hub 的平台 adapter 会在进入严格缓存前把 AppHint 解析成 AppIdentity（ADR-034、collection/CONTEXT.md），这是 Hub 改写采集器 payload。Fact Model 只允许 Hub 附加 ReceivedAt。需要定：这类富化是留在 legacy adapter 边界内，还是作为独立的派生层，抑或承认 Hub 对某些 schema 有改写权。§10 的现有模型映射表里现在完全没有这一行。
6. **ADR-021 的 Active 读模型没有对应物。** 现在 Active 由 hub 的 per-Source last-seen 判定，新鲜度窗口从**采集器自报**的 `flushPeriodMs` 派生；新协议里 `flushPeriodMs` 变成 Hub 通过 `spec.config` 下发给采集器，方向反了。同时 `activation.health` 明确“不等于 Source 最近产生了 Fact”，而 ExternalHost binding 没有连接概念，协议也没规定 Activation 因何超时结束——浏览器关掉之后那个 Activation 永远停在 Ready。Current Activity 与 presence 通道同样不属于三类 Fact，协议里没有归宿。
7. **`Collector Capability` 撞名。** collection/CONTEXT.md 里它是“用户可理解的一项观测深度”，带 enabled 与实际运行状态；协议 §3.2 里它是 wire 层的扩展能力协商。两者在同一份实现里会同时出现，按 domain.md 的规矩必须拆开命名。
8. **dimension“低基数”没有执行点。** Fact Model §3.2 要求 identifying dimensions 低基数，但除了“值必须是 string”之外没有任何长度、数量或每 Stream 上限的约束，Hub 也没有对应的拒绝码。基数爆炸恰好是 research 里点名的经典失败。
9. **Collector Instance 与 packageId 的绑定没定义。** 错误码里有 `package_mismatch`，暗示 Instance 记着一个期望的 packageId；但本文只说“Package 更新不改变 InstanceId”，没说换成另一个 packageId 算不算同一个 Instance。这直接决定 StreamId 能不能跨包保持稳定。
10. **Manifest 的完整形状无人认领。** 本文说字段与示例只在协议 §10 维护，而 §10 标题就叫“协议相关最小面”，`requires.agent`、磁盘需求、覆盖完整度、权限占位都不在里面。要么承认 Manifest 还差一份独立草案，要么把这些字段补进 §10。

## 下一触发点

停止继续扩张架构 grilling。下一步根据已确认的 ADR-040 / ADR-041 起草 Collector Protocol 消息、Manifest 与三类 Fact schema；在 schema 草案暴露真实矛盾前，不再追问包市场、安全、GC 或存储实现细节。

**触发点已经命中。** 两份草案写完后暴露的矛盾见上一节，第 1、2、3、4 条会改动协议或 Fact 信封本身，应当先裁决再动手实现；第 5～10 条可以在实现期分批处理。裁决完成后，「首阶段建议」的五条按 `docs/agents/issue-tracker.md` 落成 `issues/01..05`。

当前草案：

- [Collector Protocol v1](./spec/collector-protocol-v1.md)
- [Fact Model v1](./spec/fact-model-v1.md)

## 研究记录

三份研究都写于 ADR-041 定名之前，文中的 `Sample` 一律读作 `Measurement`；它们是当时的调查快照，不随决策更新。

- [DeepSeek Harness / Cordis 论文与实现评估](./research/deepseek-harness-and-cordis.md)
- [Prometheus、OpenTelemetry 与 Grafana 事实模型](./research/observability-fact-models.md)
- [ActivityWatch 数据模型](./research/activitywatch-data-model.md)
