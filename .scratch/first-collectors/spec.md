# 首批采集来源

## 已确认范围

- 首批来源为系统采集器和 AI 会话记录。
- 系统采集器目标包括桌面活动：前台应用时段、窗口标题时段、键鼠事件时间点和设备休眠／唤醒状态；锁屏的具体规则待确认。
- 系统采集器首批支持 macOS。
- AI 会话首批来源为 Codex 和 Claude Code 的本地会话记录。
- AI 会话首批保留消息原文、工具调用和结果，以及来源已有的身份、时间和上下文。会话概况用于查找，明细单独保留。
- 优先实现实际形成观测的 Observer；运行时底座等多个 Observer 的共用需求明确后再考虑。
- 首个实现先采集一次真实 macOS 前台应用读数，经现有 API 提交、保存并读回核对。观测表达“在时刻 t 看到应用 A 位于前台”，`StartAt = EndAt = t`；持续观察的区间规则随后确定。
- 前台应用读数保留 `bundleIdentifier`、`name` 和 `executablePath` 三个原始字段；系统未提供的字段保存为 `null`，时间由 Observation 保存。
- Observer 按形成观测的责任划分，API、数据源或输出类型不直接决定主体身份。前台状态 Observer 负责应用和窗口标题两类观测，共用 ObserverId，分别使用自己的 Schema 与时间线。标题读取失败时仍可形成应用观测。输入事件 Observer 负责键鼠事件时间点，设备状态 Observer 负责休眠／唤醒状态时间段；三个主体可运行在同一程序中。当前本地身份属于这台 Mac 上的前台状态实例；多次采集、重启或升级沿用同一 ID。
- 后续应用使用先表示前台时长。应用时段从第一次确认在前台时开始，确认切换时结束；休眠、停止采集或读取失败时，在最后一次确认的位置断开，恢复后新开一段，中间空白不计入。是否排除锁屏或无输入时段留给后续分析规则。应用时段的起止时间只放在 Observation 中。连续采集未中断且完整应用数据相同时延长同一段；数据变化则新开一段，每段数据保持一致。当前单次读数仍使用三个原始字段；按用户要求，时段文档已给出 name 加 identifiers[] 的跨平台数据结构草案，字段约束、Scheme 与列表相等规则待审阅确认。不同名称或安装路径的软件归类由后续查询规则处理。采样间隔和长时间未取得读数的处理待确认。应用时段表达不同事实，需要自己的 Schema 和 ID，当前单次读数保持原含义。时段定义单独记录在 `observers/schemas/foreground-application-interval.mdx`，尚未实现。
- 当前应用与标题可以由采集器内存提供，不要求持久化每次采样；历史 Observation 引用当时的数据，不能通过覆盖同一个“当前 Data”改变历史。结束未知不能证明当前状态。设备清醒、会话未锁定和近期输入是不同事实，“用户活跃”判断规则尚未确定。
- 采集器首次启动生成 UUIDv7 并保存到本地身份文件，后续启动读取同一文件。另一个独立实例使用自己的身份文件；文件损坏时报告错误，不自动更换身份。
- 当前前台状态 Observer 的身份文件默认位于 `~/Library/Application Support/Heartbeat/observers/macos-system/identity.json`，继续复用已有 UUID；允许启动时指定其他身份文件，从而选择另一个独立实例。其他独立主体实例使用自己的身份。增加输出类型本身不要求新建身份；身份不依赖代码所在目录。
- 每次实际读取前台应用，都创建新的 Data 和 Observation，即使连续读到同一个应用也各自保留当时读数。重试提交同一份读数时沿用原 ID 和内容，不重新采集；本轮不引入应用去重或历史版本机制。
- 首个实现为独立 .NET 命令行程序，位于 `src/Observers/Heartbeat.Observer.ForegroundState`。复用 Core 的实体和 UUIDv7，直接调用 macOS AppKit 读取前台应用，经 HTTP 提交，运行一次后退出。读取、身份文件和提交逻辑留在具体 Observer 内。
- “前台应用单次读数”使用平台无关的 ObservationSchema，遵循相同字段、约束和时间规则的平台实现共用固定 SchemaId `01a114c5-6282-7385-ab65-b93517e16b0f`。名称为 `foreground application reading`，定义不包含 macOS 或具体系统 API；当前采集实现仍只支持 macOS。定义描述三个可空字段及“在时刻 t 读到某应用位于前台”的事实含义。语义或约束变化时遵循现有模型约定创建新的定义和 ID。
- 系统返回前台应用时，缺少的属性保存为 null；系统没有返回前台应用或读取出错时，报告采集失败，不创建 Data 和 Observation。读取失败属于诊断，不表达为三个字段均为空的应用读数。
- 本轮只做单次提交：提交失败时报告出错步骤和本次实体 ID，以失败状态退出；已成功保存的实体保留，不回滚。重新运行会重新采集并形成新的读数。自动重试和离线队列暂缓；后续若重试同一份读数，仍须沿用原 ID 和内容。
- 具体采集来源的文档放在与 Core 同级的独立 Observers Layout Tab；Core 的 Observer 和具体数据页面链接到具体实现。导航调整已完成，文档类型检查、构建、栏目切换和具体数据章节跳转均通过。
- 使用当前 Core 模型、实体保存与读取协议。
- 按项目规则逐项确认采集内容、领域语义和组件职责后实施。
- HTTPS 转发头、GHCR 和部署延后；Disslopify 继续暂缓。

## 当前基础

- 已有 Observer、ObservationSchema、Observation 和通用领域数据的保存入口，以及按实体标识读取。
- 实体标识由采集程序生成；重试或重放已有输入沿用已有标识映射。
- 观测记录事实时间；缺失边界保持未知。
- 旧采集器代码仅作为历史调研材料，当前实现以站内模型与协议为准。

## 待逐项确认

1. 跨平台 Schema 的字段、采样间隔与采集停顿处理，以及窗口标题、输入事件、锁屏／休眠的具体规则。
2. 各领域的实体身份、记录粒度、更新和历史保留规则。
3. 持久化标识映射、离线保存与提交的职责。
4. 查询、展示与本轮验收边界。

## Observer 文档与后续交付方向

用户将具体 Observer 文档的主要问题收拢为三个：ObserverId 如何取得、ObservationSchema 定义什么、ITimed 如何实现。Data 的结构与约束属于 ObservationSchema 的一部分。Observers 左侧按指南、Observers、ObservationSchemas 分区；每份 Schema 在 `observers/schemas/` 独立成页，Observer 页面链接到所用定义。侧栏现有三个目标 Observer 和四份目标 Schema 页面；字段与采集规则未确认部分标为草案。单次读数的独立 Schema 页已按用户要求移除，现有代码契约收拢到前台状态页面的“当前单次读数”节。提交与失败暂不展开；现有提交行为继续记录在模块 README 和 ADR 0002。

用户确认 Observer 按形成观测的责任划分，同一主体可以使用多份 Schema，平台客户端组合运行各主体。客户端只组合或传输时保留原身份；实际形成新聚合观测的主体可以拥有自己的 ObserverId。聚合数据保存具体实例引用，ObservationSchema 定义引用字段。当前不引入聚合 Observer，分工记录在 ADR 0002；长期文档改为 Observers 概览与前台状态 Observer 页面。

用户确认当前采集器扩展为前台状态 Observer，程序集按主体职责命名。当前项目与程序集为 `Heartbeat.Observer.ForegroundState`，命名空间为 `Heartbeat.Observers.ForegroundState`。现有读取实现明确命名为 `MacOSForegroundApplicationReader`；当前仍只支持 macOS，没有引入空的平台接口。

用户提出参考 ActivityWatch，并确认前台状态 Observer 同时负责应用和标题，共用身份，保留两类观测与各自事实时间；标题变化不切开应用的前台时间段，标题读取失败不影响应用采集。决定记录在 ADR 0002。调查事实与建议见 [ActivityWatch 参考笔记](./activitywatch-notes.md)和[时间与活跃建模笔记](./activitywatch-time-model-notes.md)。应用时段的基本边界已确认；标题数据结构、缺失信息表达与具体时间边界仍待逐项确认。当前仍为原有单次应用读数。

用户提出后续使用本地 SQLite 保存待交付观测：收集后在本地保留，服务端确认送达后移除，未确认的内容后续继续发送。该方向尚未实施，也尚未确定具体契约。

后续需逐项确定：本地保存与发送的顺序；本地记录是否包含 Observation 及其引用实体的完整内容；服务端确认的范围与依据。当前实现仍为单次 HTTP 提交与读回核对，不含 SQLite 或 Outbox。

## 推进顺序

当前 macOS 程序运行一个前台状态 Observer，已实现单次读取、形成实体、HTTP 提交和读回核对。后续先设计跨平台 Schema，再确定应用时段的采集规则以及窗口标题、输入事件和设备状态规则，逐项推进实现；Codex 和 Claude Code 会话采集仍在首批范围内。

用户要求先从整个系统的实际数据链路出发，撤回空运行时宿主。运行时及插件机制留待多个 Observer 的实际共用需求出现后再决定；当前不建设调度、共用本地存储或交付底座。

跨平台应用时段的数据结构草案已按用户要求写入站内 `observers/schemas/foreground-application-interval.mdx` 供审阅，未确认部分明确标注为草案。其他 Schema 与研究笔记见 [跨平台桌面观测定义草案](./cross-platform-schemas.md)。

## 前台应用字段的用途

- `bundleIdentifier` 保留系统提供的应用标识。
- `name` 保留便于人阅读的应用名称，不用于确定实体身份。
- `executablePath` 保留本机具体程序的位置，帮助解释缺少 bundle identifier 或存在多个安装副本的读数。

三个字段都是来源提供的观测内容，不直接作为 Heartbeat 的实体标识，也不承诺跨设备唯一性或长期稳定性。

## 首个实现的完成证据

- 按用户要求移除单次应用读数的独立 Schema 页面，现有 ID、字段、快照和时间点契约收拢到前台状态页面的“当前单次读数”节；没有修改代码或数据库定义。新增输入事件、设备状态两个 Observer 页面，以及标题、键鼠事件、设备状态三份 Schema 草案，侧栏现为三个 Observer 与四份目标 Schema。文档类型检查和构建通过，10 个 JSON 代码块可解析，86 个相关本地链接／锚点有效；实际浏览器已检查新增页面、侧栏、草案标识、旧地址 404 和当前实现契约。截图：`TestResults/mac-observer/observer-schema-suite.png`。本轮未运行原生采集或后端测试。
- 按用户要求，将 `name` 与 0～N 个 `{ scheme, value }` 标识线索的跨平台草案写入应用时段 Schema 页面，包括 JSON 结构、macOS／Windows／空列表示例与时间边界；同步 Observer、概览、Core 定义页和 ADR 引用，标明尚未实现。文档类型检查与构建通过，4 个 JSON 代码块可解析，28 个本地链接／锚点有效，实际页面已检查字段表、侧栏、示例和草案状态。截图：`TestResults/mac-observer/cross-platform-application-schema.png`。列表相等规则与固定 SchemaId 待确认；本轮未修改应用代码或数据库定义，未运行原生采集或后端测试。
- 完整读数分段已确认并同步至时段 Schema、Observer 与 ADR：连续且三个字段相同延长同一段，字段变化新开一段；软件归类由查询规则处理。文档类型检查、构建与实际页面检查通过，截图：`TestResults/mac-observer/foreground-application-segmentation.png`。跨平台字段替换方案保留在 scratch，尚未修改永久字段、代码或数据库定义；本轮未运行原生采集或后端测试。
- 应用时段的三个原始字段及“时间只放 Observation”已确认，已在 ObservationSchemas 分区新增独立页面 `foreground-application-interval.mdx`，同步 Observer、概览、Core 定义页与 ADR 引用。文档类型检查、构建和 28 个相关本地链接／锚点检查通过，实际浏览器已核对侧栏、字段和 Observer 引用。截图：`TestResults/mac-observer/foreground-application-interval-schema.png`。固定 SchemaId 尚未分配，代码和数据库定义未改；本轮未运行原生采集或后端测试。
- 本轮只更新文档，记录三个桌面 Observer 的职责、四类目标观测与已确认的应用时段边界；当前代码和时间点 Schema 未变。文档类型检查与构建通过，18 个相关本地链接与锚点有效。实际浏览器已核对职责表、应用时段章节跳转、ADR 和当前 Schema 的 ID／时间点含义。截图：`TestResults/mac-observer/desktop-observation-goals.png`、`TestResults/mac-observer/foreground-interval-boundaries.png`。连续采集、标题、输入事件和设备状态尚未实现，本轮未运行原生采集或后端测试。
- 用户确认将应用读数定义改为平台无关的“前台应用单次读数”，名称为 `foreground application reading`，继续沿用 `01a114c5-6282-7385-ab65-b93517e16b0f`。代码与文档已去掉定义中的 macOS 和 NSWorkspace 信息，并明确 bundle identifier 的字段原义；平台读取方式仍记录在具体 Observer 中。已核对 ID、JSON 字段与约束保持一致，26 个相关本地链接与锚点有效。
- 本次完整构建、67 项集成测试、27 项开发工具测试、文档类型检查和构建均通过。质量扫描完成，无新增复杂度或疑似未使用发现，整体工作树相对 HEAD 有 1 处测试重复提示，涉及 `EntityApiTests` 与 `ObservationSchemaApiTests` 的错误响应断言辅助方法。证据：`.artifacts/verification/20261007T110157Z-verify-closeout-18cd88c846d24ea985a6e1507b6ace00/`。实际浏览器已核对平台无关的页面标题、名称、ID 和侧栏；截图：`TestResults/mac-observer/foreground-application-schema.png`。当前仍只实现 macOS 应用读取，本次未重跑原生采集。
- 按用户要求，Observers 左侧导航采用与 Core 相同的分区方式：指南、Observers、ObservationSchemas。应用 Schema 已拆为 `observers/schemas/foreground-application.mdx` 独立页面，原合并页已删除，相关引用已更新。文档类型检查与构建通过；实际浏览器已核对分区、独立页面及跳转。截图：`TestResults/mac-observer/observer-sidebar-sections.png`。
- Observers 栏目新增观测定义目录，将已实现的应用 Schema 的 ID、名称、字段、粒度与事实时间集中登记；Core、Observer、ADR 与模块 README 已链接到定义页。文档类型检查和构建通过，54 个相关本地链接与锚点有效；实际浏览器已检查栏目导航、目录到定义详情的跳转及 Observer 的引用。截图：`TestResults/mac-observer/observation-schema-catalog.png`。本次仅整理已有定义，标题 Schema 仍待确认。
- 按形成观测的责任调整模型与 ADR，并将项目改为 `Heartbeat.Observer.ForegroundState` 后，完整构建、71 项集成测试、27 项开发工具测试、文档类型检查与构建均通过；质量扫描无新增发现。证据：`.artifacts/verification/20261007T101432Z-verify-closeout-0a274bed15234cb2994c7fd13be79336/`。
- 前台状态程序集在 macOS 上通过独立 API 与 PostgreSQL 完成 7 次真实应用读取、提交和读回核对，覆盖重启身份复用、独立实例、4 进程并发首次创建和失败退出；已读回新的主体名称，应用 Schema 保持原 ID。证据：`TestResults/mac-observer/46ba25bc-f95a-424f-92c4-6c09615130a4/`。独立环境已清理。
- 已核对新程序集名称、命令行帮助、Observers 概览、前台状态页面与 ADR 0002；页面截图为 `TestResults/mac-observer/foreground-state-responsibility.png`。标题采集、连续采集和休眠尚未实现。
- 程序集改名后，完整构建、71 项集成测试、27 项开发工具测试、文档类型检查与构建均通过，质量扫描无新增发现。证据：`.artifacts/verification/20261007T095638Z-verify-closeout-74711e7d85494215b999afe553b92d18/`。
- 新程序集已在 macOS 上通过独立 API 与 PostgreSQL 完成 7 次真实读取、提交和读回核对，覆盖重启身份复用、独立实例、4 个进程并发首次创建和失败退出。证据：`TestResults/mac-observer/93ea14cf-9c80-41ef-9cdf-9bc91552206d/`。独立环境已清理。
- 构建属性和命令行帮助已确认新程序集名称；运行说明与项目引用已同步。46 个本地文档链接与章节锚点有效，实际页面已检查新的项目路径。
- 能力与平台客户端分工调整后，完整构建、71 项集成测试、27 项开发工具测试、文档类型检查与构建均通过。质量比较无新增重复、复杂度热点或疑似未使用发现。证据：`.artifacts/verification/20261007T094743Z-verify-closeout-1085aa8391454b9c965d41ca1f2172ee/`。
- 浏览器已检查 Observers 概览、前台应用能力页、栏目导航和 ADR 0002 跳转；改动文档中的 66 个本地链接与章节锚点有效。命令行帮助已确认身份文件指向前台状态 Observer。此次未重跑 macOS 原生采集，原生读取代码未改动。
- 完整构建、71 项集成测试、27 项开发工具测试、文档类型检查与构建均通过。质量扫描完成，与基点 `1fbd1f12` 比较没有新增复杂度热点、重复或疑似未使用代码发现。
- 收口证据：`.artifacts/verification/20261007T051815Z-verify-closeout-b2eb6f9aeed6454381fcabee269ae30f/`。
- 实际 macOS 原生读取到 Google Chrome；独立 API 和 PostgreSQL 上 7 次采集均提交并读回核对通过。包含重启沿用身份、另一身份文件选择独立实例、4 个进程同时首次启动使用同一实例身份，以及失败退出与损坏文件保留检查。独立环境已清理，未向日常数据库写入测试数据。
- 原生验证证据：`TestResults/mac-observer/753263aa-d991-485a-9401-bf91150e78f2/`；可重复脚本为 `TestResults/mac-observer/verify-native.py`。
- 并发身份用例先失败（8 个不同 ID），修复创建过程的进程间互斥后通过；跨进程实际运行也验证通过。
- 实际浏览器已检查 `/observers/macos`、当时的具体 Observer ADR 链接（现合入 ADR 0002）及 `/testing` 的新验证边界。未现场触发系统不返回前台应用的状态；连续采集、窗口标题、锁屏／休眠和 AI 会话采集尚未实现。

本规格记录进行中的工作；前台应用的已确认长期语义已写入文档站 `observers/foreground-state.mdx` 和 ADR 0002，能力与平台客户端分工见 ADR 0002。首批采集来源全部完成后删除本目录。
