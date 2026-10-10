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
- 后续应用使用先表示前台时长。应用时段从第一次确认在前台时开始，确认切换时结束；休眠、停止采集或读取失败时，在最后一次确认的位置断开，恢复后新开一段，中间空白不计入。是否排除锁屏或无输入时段留给后续分析规则。应用时段的起止时间只放在 Observation 中。连续采集未中断且完整应用数据相同时延长同一段；数据变化则新开一段，每段数据保持一致。当前单次读数仍使用三个原始字段。应用身份改为分层：本机应用实体表示某台设备上的一个安装，两台 Mac 上的 VS Code 分别拥有本机应用实体，可以关联到同一个跨平台应用实体。identifiers 移到本机应用实体，观测保留当时内容并引用应用。观测字段确认为 localApplicationId 与 name，name 未知时为 null。本机识别和关联维护规则待确认。之前 name 加 identifiers 的观测结构不再作为最终契约。跨平台应用的关联由后续规则处理。采样间隔和长时间未取得读数的处理待确认。应用时段表达不同事实，需要自己的 Schema 和 ID，当前单次读数保持原含义。时段定义单独记录在 `observers/schemas/foreground-application-interval.mdx`，尚未实现。
- 当前应用与标题可以由采集器内存提供，不要求持久化每次采样；历史 Observation 引用当时的数据，不能通过覆盖同一个“当前 观测内容”改变历史。结束未知不能证明当前状态。设备清醒、会话未锁定和近期输入是不同事实，“用户活跃”判断规则尚未确定。
- 采集器首次启动生成 UUIDv7 并保存到本地身份文件，后续启动读取同一文件。另一个独立实例使用自己的身份文件；文件损坏时报告错误，不自动更换身份。
- 当前前台状态 Observer 的身份文件默认位于 `~/Library/Application Support/Heartbeat/observers/macos-system/identity.json`，继续复用已有 UUID；允许启动时指定其他身份文件，从而选择另一个独立实例。其他独立主体实例使用自己的身份。增加输出类型本身不要求新建身份；身份不依赖代码所在目录。
- 每次实际读取前台应用，都创建新的 观测内容 和 Observation，即使连续读到同一个应用也各自保留当时读数。重试提交同一份读数时沿用原 ID 和内容，不重新采集；本轮不引入应用去重或历史版本机制。
- 首个实现为独立 .NET 命令行程序，位于 `src/Observers/Heartbeat.Observer.ForegroundState`。复用 Core 的实体和 UUIDv7，直接调用 macOS AppKit 读取前台应用，经 HTTP 提交，运行一次后退出。读取、身份文件和提交逻辑留在具体 Observer 内。
- “前台应用单次读数”使用平台无关的 ObservationSchema，遵循相同字段、约束和时间规则的平台实现共用固定 SchemaId `01a114c5-6282-7385-ab65-b93517e16b0f`。名称为 `foreground application reading`，定义不包含 macOS 或具体系统 API；当前采集实现仍只支持 macOS。定义描述三个可空字段及“在时刻 t 读到某应用位于前台”的事实含义。语义或约束变化时遵循现有模型约定创建新的定义和 ID。
- 系统返回前台应用时，缺少的属性保存为 null；系统没有返回前台应用或读取出错时，报告采集失败，不创建 观测内容 和 Observation。读取失败属于诊断，不表达为三个字段均为空的应用读数。
- 本轮只做单次提交：提交失败时报告出错步骤和本次实体 ID，以失败状态退出；已成功保存的实体保留，不回滚。重新运行会重新采集并形成新的读数。自动重试和离线队列暂缓；后续若重试同一份读数，仍须沿用原 ID 和内容。
- 具体采集来源的文档放在与 Core 同级的独立 Observers Layout Tab；Core 的 Observer 和观测内容页面链接到具体实现。导航调整已完成，文档类型检查、构建、栏目切换和观测内容章节跳转均通过。
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

用户将具体 Observer 文档的主要问题收拢为三个：ObserverId 如何取得、ObservationSchema 定义什么、ITimed 如何实现。观测内容 的结构与约束属于 ObservationSchema 的一部分。Observers 左侧按指南、Observers、ObservationSchemas 分区；每份 Schema 在 `observers/schemas/` 独立成页，Observer 页面链接到所用定义。侧栏现有三个目标 Observer 和四份目标 Schema 页面；字段与采集规则未确认部分标为草案。单次读数的独立 Schema 页已按用户要求移除，现有代码契约收拢到前台状态页面的“当前单次读数”节。提交与失败暂不展开；现有提交行为继续记录在模块 README 和 ADR 0002。

用户确认 Observer 按形成观测的责任划分，同一主体可以使用多份 Schema，平台客户端组合运行各主体。客户端只组合或传输时保留原身份；实际形成新聚合观测的主体可以拥有自己的 ObserverId。聚合数据保存具体实例引用，ObservationSchema 定义引用字段。当前不引入聚合 Observer，分工记录在 ADR 0002；长期文档改为 Observers 概览与前台状态 Observer 页面。

用户确认当前采集器扩展为前台状态 Observer，程序集按主体职责命名。当前项目与程序集为 `Heartbeat.Observer.ForegroundState`，命名空间为 `Heartbeat.Observers.ForegroundState`。现有读取实现明确命名为 `MacOSForegroundApplicationReader`；当前仍只支持 macOS，没有引入空的平台接口。

用户提出参考 ActivityWatch，并确认前台状态 Observer 同时负责应用和标题，共用身份，保留两类观测与各自事实时间；标题变化不切开应用的前台时间段，标题读取失败不影响应用采集。决定记录在 ADR 0002。调查事实与建议见 [ActivityWatch 参考笔记](./activitywatch-notes.md)和[时间与活跃建模笔记](./activitywatch-time-model-notes.md)。应用时段的基本边界已确认；标题数据结构、缺失信息表达与具体时间边界仍待逐项确认。当前仍为原有单次应用读数。

用户提出后续使用本地 SQLite 保存待交付观测：收集后在本地保留，服务端确认送达后移除，未确认的内容后续继续发送。该方向尚未实施，也尚未确定具体契约。

后续需逐项确定：本地保存与发送的顺序；本地记录是否包含 Observation 及其引用实体的完整内容；服务端确认的范围与依据。当前实现仍为单次 HTTP 提交与读回核对，不含 SQLite 或 Outbox。

## 推进顺序

当前 macOS 程序运行一个前台状态 Observer，已实现单次读取、形成实体、HTTP 提交和读回核对。后续先设计跨平台 Schema，再确定应用时段的采集规则以及窗口标题、输入事件和设备状态规则，逐项推进实现；Codex 和 Claude Code 会话采集仍在首批范围内。

用户要求先从整个系统的实际数据链路出发，撤回空运行时宿主。运行时及插件机制留待多个 Observer 的实际共用需求出现后再决定；当前不建设调度、共用本地存储或交付底座。

应用身份分层与设备内安装范围已确认，已写入站内 `observers/schemas/foreground-application-interval.mdx` 与 ADR 0002。观测字段确认为 localApplicationId 与 name。本机应用实体更新和跨平台关联规则待确认。文档使用 STC v0.2 与实际数据示例，不展示 JSON Schema 定义。其他观测定义与研究笔记见 [跨平台桌面观测定义草案](./cross-platform-schemas.md)。

## 前台应用字段的用途

- `bundleIdentifier` 保留系统提供的应用标识。
- `name` 保留便于人阅读的应用名称，不用于确定实体身份。
- `executablePath` 保留本机具体程序的位置，帮助解释缺少 bundle identifier 或存在多个安装副本的读数。

三个字段都是来源提供的观测内容，不直接作为 Heartbeat 的实体标识，也不承诺跨设备唯一性或长期稳定性。

## 首个实现的完成证据

- 按用户要求，在三个 Observer、一个本机应用实体与四份观测定义页面顶部加入 Mermaid 结构图：接口在左、当前对象在中、字段在右。主图维护在 `observers/relations.json`，页面通过 `ObserverStructure` 引用节点；接口名称与链接复用 Core 主图，概览不展示整图。草案字段标明草案，观测内容 图不加入事实时间字段。八页渲染、接口跳转与概览已在实际浏览器检查。Developer CLI 收口通过文档类型检查、构建和质量观察，无新增复杂度、重复或疑似未使用发现。证据：`.artifacts/verification/20261009T091700Z-verify-closeout-6d9d618c054946538a0f81e56ece9716/`。截图：`TestResults/mac-observer/observer-page-structure.png` 与 `TestResults/mac-observer/application-data-structure.png`。本轮未运行采集程序、后端测试或容器启动。
- 用户确认 `localApplicationId` 与 `name`，实体命名为 `LocalApplication`（本机应用）。Observers 侧栏新增 Entities 分区和本机应用独立页面；应用时段页展示名称与空名称两个 JSON 示例，标识线索与跨平台关联说明归入实体页。术语表、ADR 0002、Core 和 Observer 引用已同步。文档类型检查和构建通过，7 个 JSON 示例可解析，77 个本地链接与锚点有效；实际浏览器核对新分区、实体页、观测页及双向链接。截图：`TestResults/mac-observer/entities-local-application.png`。构建期间社交分享图片的外部字体下载失败，页面构建仍成功；本轮仅改文档，未运行原生采集或后端测试。
- 用户确认本机应用实体表示设备上的应用安装：两台 Mac 上的 VS Code 分别保留实体身份，可以关联到同一个跨平台应用实体。术语表、ADR 0002、应用时段、Observer 与 Core 引用已同步。应用时段的引用字段仍待确认，当前单次读数代码与数据库定义未变。文档类型检查和构建通过，5 个 JSON 示例可解析，71 个本地链接与锚点有效；实际浏览器核对应用时段、术语表、ADR、前台状态与概览。截图：`TestResults/mac-observer/native-application-identity.png`。本轮未运行原生采集或后端测试。
- 用户已确认应用时段的 `name`、`identifiers`、`scheme`、`value` 结构与空值规则。Observer 文档按用户提供的 STC v0.2 整理：使用固定术语、先写条件、拆分独立命题、删除重复说明；定义页只展示数据示例与文字规则。应用字段标为已确认，列表比较、Scheme 命名及采集规则仍待确认。文档类型检查和构建通过，9 个 JSON 示例可解析，54 个本地链接与锚点有效；实际浏览器已核对八个页面的确认状态、草案状态和数据示例。截图：`TestResults/mac-observer/stc-observer-docs.png`。本轮未修改应用代码，未运行原生采集或后端测试。
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

## 观测内容与业务链

Observation 通过 ContentId 引用业务定义的普通实体，HTTP 名称为 contentId。“观测内容”是实体在观测中的角色，Core 不提供内容基类。观测链与业务链在此交汇；业务提交入口与存储组织继续逐项讨论。职责已写入 ADR 0010。

## 保存入口边界

删除 PUT /entities/{id} 的 HTTP 入口与 OpenAPI 声明；保留 IEntityStore.SaveEntityAsync、统一读取及通用存储事务。业务实体保存测试直接调用内部存储。用户明确将采集程序提交方式留到下一项讨论，本轮保留程序并验证业务内容提交收到 405 时明确失败。

## EntitySchema 驱动业务表

用户确认建设动态数据库建模系统。现有 ObservationSchema 演进为 EntitySchema，统一定义业务实体的结构、引用、约束和含义。每个业务 SchemaId 对应一张表；同 ID 的模型固定，模型变化使用新 ID 和新表。表名属于存储配置，Core 不依赖数据库类型。模型声明的具体字段、引用元数据、HTTP 路径和建表时机继续逐项确定。

### 工作顺序

先完成模型文档和 API 契约，再讨论存储，最后修改应用代码。API 命名同步为 EntitySchema、entity_schema 与 /entities/schemas/{id}。Observers 侧栏分区为 EntitySchemas。模型声明对象的内部字段、引用目标类型的表达和业务实体提交入口继续逐项讨论。

### EntitySchema 属性展开

取消 properties.schema，直接使用 properties.name 与 properties.fields。fields 以业务字段名映射字段定义。接口示例使用 title: { type: string }。字段类型集合、必填、空值和实体引用的内部表示继续逐项确定。本轮只同步文档和 OpenAPI。

### EntitySchema 固定存储

EntitySchema 本身保存在系统预先建立的 entity_schemas 表，包含 id、name、fields。fields 用 jsonb 整体保存字段定义；后端根据定义建立业务表并将业务数据映射到列。固定表由系统确定结构，不递归要求另一份 EntitySchema。定义表页、存储导航、Core 与 ADR 0011 已同步；应用代码与完整业务存储方案随后处理。

### 字段与引用统一声明

普通属性和实体引用都在 fields 中定义，通过 type 区分。type: reference 声明实体引用，业务实体的目标标识放在 references 中；普通属性值放在 properties 中，不重复保存。字段类型集合、引用目标模型、必填与空值规则继续逐项确定。

### 必填与空值约束

字段定义使用 required 判断字段是否必须出现，使用 nullable 判断字段值是否允许为 null。两者分别约束，适用于普通属性与引用。API 与模型文档已同步，读取协议允许字段定义许可的空引用；配置缺省规则继续单独确定。

### 字段定义配置必填

每个字段定义必须明确提供 type、required、nullable，三项配置没有隐含默认值。缺少任一配置时，请求不合法。已同步 OpenAPI 约束、模型正文和存储示例。

### 字段类型与列映射

字段类型为 string、boolean、integer、number、reference、object、array。前五种类型分别映射 PostgreSQL text、boolean、bigint、numeric、uuid。object 默认使用 jsonb，允许通过存储配置展平到同一业务表的多列；array 使用 jsonb。内部结构按模型声明校验，不自动拆表。数值范围、对象成员和数组元素的声明随后逐项确定。

### 对象结构与存储映射

object 描述业务结构，jsonb 只是默认存储映射。存储配置可以将对象展平为同一张业务表的多列，读取时还原业务对象，并保留省略与空值的语义。具体配置格式与还原方式随后确定；本轮未改变数组存储约定。对象成员的声明见后续“对象成员格式”。

### 对象成员格式

object 通过 fields 声明成员，键为成员名，值为字段定义；成员继续明确提供 type、required、nullable。已同步 OpenAPI 字段定义、保存与读取示例、模型正文和 ADR。数组元素声明与引用位置按后续“实体引用位置”约定。

### 数组元素格式

array 必须提供 items，元素定义明确提供 type、nullable，不使用 required。外层 required 表示数组字段必填，nullable 表示数组本身允许为空，items.nullable 表示元素允许为空。对象元素用 items.fields 声明成员，成员沿用完整字段定义。数组必填不表示至少一项。引用位置按后续“实体引用位置”约定。

### 实体引用位置

reference 只允许出现在实体顶层字段，对象成员和数组元素禁止 reference，适用于任意嵌套层级。HTTP references 继续使用名称到 UUIDv7（允许空值时为 null）的映射，不引入嵌套路径或引用数组。该约束适用于 EntitySchema 的模型声明与 HTTP 表示，不改变 IEntity.GetReferences 的枚举接口。

### 业务资源与 Schema 同步登记

业务保存入口为 PUT /entities/{resourceName}/{id}，每个唯一资源名绑定一个 SchemaId，避开 schemas、observers、observations。上传 EntitySchema 时通过 properties.resourceName 一起登记。定义保存、资源登记、首次建表全成功才返回成功，失败时整体回滚，资源名被其他 Schema 占用返回 409。定义表增加 resource_name 保存资源绑定；资源名与显示名称分开，默认物理表名按后续命名规则生成。已同步 API、模型和相关 ADR。

### 资源名规则

resourceName 只使用小写英文字母、数字和连字符，以字母开头，全局唯一，保留 schemas、observers、observations。插件可自行加前缀，系统不另设命名空间。name 用于展示，可使用中文；resourceName 用于接口路径；数据库表名由存储映射决定。

### 资源名登记后固定

同一 SchemaId 的 resourceName 首次登记后固定，后续提交必须保持原值；修改 name 不改变模型含义时可以保留 SchemaId，资源名和接口地址保持不变。尝试修改已登记的资源名时返回 409 Conflict，保留原定义与资源绑定。

### 未登记的业务资源

向未登记的 resourceName 提交业务实体时返回 404 Not Found，不创建实体内容、统一实体索引或业务表。

### 默认业务表名

默认物理表名为 entity_ 加资源名，将资源名中的每个连字符替换为下划线。例如 local-applications 对应 entity_local_applications。接口路径继续使用原资源名。后端在登记时检查生成表名的长度及重名，失败时遵循原子登记规则。SchemaId 标识模型，显示名称变化不影响资源名和表名。

### 默认业务列名

顶层业务字段转换为 snake_case 作为默认列名，普通属性与引用字段使用相同规则。id 列保留给实体自身标识；转换后重名、占用 id 或列名超长时拒绝登记，不自动改名。对象和数组默认各占一列；对象展平后的列名由展平配置指定。

### 顶层字段省略状态

每张业务表使用系统列 __omitted_fields 记录全部省略的顶层业务字段原名，没有省略时为空列表。后端按本次完整提交生成，普通属性与引用统一处理，并与业务值和统一索引在同一事务中保存；列表和列值必须一致。系统列不进入业务请求或响应，业务字段不能占用它。嵌套 JSON 自行保留内部成员状态，展平对象的状态表示随配置确定。系统列使用 text[] NOT NULL。

## API 与动态存储实施范围

未声明字段（含嵌套成员）返回400；业务引用只校验非空UUIDv7，允许悬空，不约束目标模型。本阶段只实现默认存储映射，对象和数组均为JSONB，自定义表名及展平配置后续讨论。由GPT-6.1 Sol实施，主Agent独立验收。现有单次前台采集适配新契约，连续采集等功能保持后续范围。

API与动态存储实现及独立验收完成：EntitySchema登记、原子建表、业务资源PUT和统一GET、精确数值、缺失/null、悬空引用、完整替换、并发事务与现有Foreground单次提交链路。最终closeout：95项集成测试、19项工具测试、文档类型/构建及质量扫描通过；证据 .artifacts/verification/20261010T114825Z-verify-closeout-389077cf889c4f6a960a795173cd02c8/。审查修复DDL并发死锁、资源末尾换行、整数表示、SQL字段花括号、引用空白、已有Schema表示错误及PostgreSQL内置列名错误映射。193条站内相对链接无失效目标，实际接口和存储页面已检查。未重跑macOS原生读取、Aspire或部署，未修改日常开发数据库。后续继续本机应用身份与连续采集等Observer业务设计。
