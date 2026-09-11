# 08 — 分析与 Dashboard 消费新事实

**What to build:** 无旧 Stream 的新观测进入适用的报表、回放、Recap、Question 和 Dashboard。分析按 Aspect 解释，未知结果完整展示，Source 保留实际来源与知识声明职责。

**Blocked by:** [01 — 独立观测保存与读取](01-independent-observation-custody.md).

Status: done

**Parent:** [Observations 全链路独立事实契约](../PRD.md)

- [x] 通过 01 的真实原生 HTTP 入口产生无旧 Stream 的代表性观测，从实际分析 API 到 Dashboard 验证结果；本项不等待 04–06 的现场生产者或 07 的维护流程。
- [x] 盘点报表、活动与输入分析、Experience/回放、Recap、Question、相关投影及 Dashboard 的活跃消费者；每个旧交付字段依赖有处理结果，不能以原始查询可读替代全体消费者验收。
- [x] 适用的 desktop-activity/input/selected-page/account-location/activity 事实参与对应已有分析；缺少旧 Stream 不导致遗漏、异常或错误归属，现有跨窗裁剪、分页与时间口径保持。
- [x] 未知 Aspect 和未知 JSON 字段完整读取/展示，不按 Source 或偶然出现的字段猜成已知活动/输入；新来源采用已有 Aspect 可进入既有解释。
- [x] Source 继续准确服务来源展示、明确筛选、深度声明及 Matcher/知识引用，与 Collector 身份、Aspect 和产品身份分别表达；不为缺少旧交付资料伪造来源。
- [x] 多窗口/多设备回放仍保留实际独立事实及必要细节证据，不因 FOI 为同一 App 而合并；设备等基础归属使用 01 已提供的准确关系，完整维护行为由 07 验收。
- [x] Recap/Question 读取和投影支持新契约，知识确认保持；若派生缓存语义受影响，按既有规则失效而非改写原 Facts 或批量调用真实 LLM。
- [x] Dashboard 的事实视图、过滤、页面关联、未知结果和空资料状态正确；请求/响应及生成客户端同步，既有活动与输入的可见行为不因新契约退化。
- [x] 以真实查询/投影和前端可观察行为完成对应回归，依照已有测试替代外部账号/LLM 调用；产品纠错与人工关联的联合回归在 09 集成验收。
- [x] 记录各消费者覆盖与尚需人工验证的步骤/承接者，不将静态编译或单个页面正常视为全部消费路径完成。

## Comments

2026-09-11：按用户批准的九项拆分创建。仅发布任务，尚未实施或执行运行测试。

### 2026-09-11 实现与验收证据

基线：`6976e10`（01 已整合）；分支：`codex/observations-08-analysis-dashboard`。
本票只维护 08，父 PRD 与整合状态由协调任务维护。以下验证均使用隔离 PostgreSQL 测试库、
原生 `POST /api/v1/observations` 与现有公开查询/投影边界；不依赖 02、04–07 的生产者或现场维护。

| 活跃消费者 | 处理与可观察验证 |
| --- | --- |
| Daily/Weekly Report、Usage/Timeline、App 明细 | `desktop-activity` 按准确 FOI/Relations 归设备与产品；空 Source 和新 Source 两组原生 HTTP 验证。跨午夜段 Usage 保留原始 180 秒、日报裁剪为 120 秒、周报 180 秒。其他 Aspect 不进入前台时长。 |
| 活动查询与回放 | selected-page/account-location/activity 原生段从 `/users/alice/segments` 返回，旧 Stream/FactId/Origin 可空，Source 如实为空；显式 Source 筛选保留，未知 Aspect 即使 Source=system 也不冒充活动。回放不再因 Source 为空丢弃事实；窗口键优先取 Collector。 |
| Input counts / key frequency | 无 Stream、无 Source 的 input Event 通过真实 HTTP 写入，键盘次数与热力键频均为 1；相同字段的未知 Aspect 不计数，半开窗终点排除事件。原输入词汇及设备统计口径保持。 |
| Experience / 原始事实查询 | 原生 503 个 Segment 跨两台机器、同 App 并行窗口，公开 Experience 分页 500+3，无重复/截断；准确设备过滤、稳定 Id/Revision、完整数组/未知 JSON 与空来源保持。 |
| Digest / Recap projection / Recap service | 原生 desktop 与 selected-page 进入主轨和语义轨；未知 Aspect 不按偶然字段解释。空 Source 深度查询不会抛异常，新 Source 的声明从 attributes.topic 取值；无真实来源的事实不假冒 Matcher 身份。 |
| Question / ActivityClusterEvidence / Matcher / Mute / 知识确认 | 同一原生事实生成确定性旁证，缺 Source 返回 null；真实 Source 的 Matcher 命中，空 Source 旁证不伪造命中；Mute 读时过滤，缓存复用不重调假 LLM。既有知识确认/纠正测试保留通过。 |
| 派生缓存 | Question payload v2→v3 按既有版本失效；Recap 知识 hash 纳入观测契约版本，旧正文只显示可重新生成。读取零生成，只有显式请求调用测试 fake；不改原 Facts、不批量调用真实 LLM。 |
| Dashboard Fact View / 过滤 / 页面关联 | 组件测试观察无 Source 的两条回放条、稳定原生 Id/Collector/Aspect、未知来源、完整嵌套 JSON、对象过滤与同 App 多窗口。标题升级仅接受 selected-page 且机器/App UUID 对齐，账号位置、无关系及异设备页面均不替换原标题。 |
| OpenAPI / generated client | SegmentResponse、AppUsageResponse、EvidenceObservationDto 的 Source 明确可空；生成客户端同步 01 ObservationSnapshot/Kind/Result。JsonElement 声明为任意 JSON，false/0/string/array/object 读取和序列化无损，不再转成对象类。 |
| PersonFactQuery / PersonSourceCount / Person UI | 按协调分工由 07 独占修复 Kind、nullable Source 与本人事实展示；08 已实际复现 Kind 返回空并交接，未重复修改。input Result alias 展示边界和未知 Event 展示也已向协调转交 07。整合后含 Person 的最终 client 由协调统一重生成。 |

失败→通过证据：

- 原生空 Source 的 Digest 首先抛 `ArgumentNullException`；修复深度表和 Matcher 可空边界后原生知识测试通过。
- 旧 Recap hash 先错误判为新鲜；加入观测契约版本后读取显示 stale 且生成调用数为 0，显式假生成后恢复新鲜。
- 前端 App 明细原生空 Source 事实从 0 条回放修复为 2 条；Fact 详情从缺失旧 FactId 修复为稳定 Id。
- 非页面、无关系和另一机器页面先错误替换前台标题；精确关系约束后 3 个组件用例通过，补充事实仍保留回放。
- 生成客户端 5 个 JSON 形状中最初 4 个失败（布尔/数字/字符串/数组），修复 OpenAPI 源并重生成后 5/5 通过。

验证入口：

- `dotnet restore Heartbeat.slnx`；`dotnet build Heartbeat.slnx --no-restore`。
- `dotnet test server/Heartbeat.Server.Tests --no-restore --filter FullyQualifiedName~IndependentAnalysis --nologo`：3/3 通过。
- Recap/Question/Depth/Knowledge/ActivityClusterEvidence + 原生知识 HTTP 相关回归：115/115 通过；最后声明取值补强后知识 HTTP 2/2 通过。
- `npm run verify`（frontend）：45 文件、306 测试、typecheck 与 production build 通过。
- `dotnet test Heartbeat.slnx --no-build --nologo`：全仓 1,375 项通过、0 失败、0 跳过。最终 OpenAPI 可空 JSON 修复后按范围补执行原生分析/知识 HTTP 5/5，以及前端 verify 306/306；没有重复未改动的 Collection 项目。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- 客户端使用 NSwag 14.7.1 从本票隔离 Development API 生成；仅规范化 OpenAPI servers 到既有 localhost 默认及清理生成尾空格。没有业务库连接。

兼容与 friction closeout：

- Stream/FactId/Origin 只用于已有历史交付资料展示，缺失保持 null；窗口键只在 Collector 未提供时使用非 legacy-import Stream。
  实际兼容对象是既有上传和历史事实；待旧协议/缓存按兼容台账退出且数据已具备准确 Collector 或接受无窗口身份装箱时移除此回落，
  以旧数据回放与原生多窗口组件测试验证。没有创建新的旧式交付字段或来源。
- 已修复触达路径的旧 Source 统计注释、不可空 DTO 与生成 JSON 丢失问题。
- KnowledgeProjection 原有字符串中的实际 NUL 替换为等价 C# `\0` 转义；基线 diff 需 `git diff --text` 阅读。
- 本票自动验证替代外部账号/LLM；实际产品纠错与人工关联的联合回归由 09 承接。
  生产部署、业务库迁移、真实安装与暂停的副本演练仍归原存储/部署门禁，本票未执行也不宣称通过。


双轴 `/code-review`（固定点 `6976e10`，规格本 Ticket08）：

- Standards：0 项硬性违反；1 项 P3 重复关系解析建议已修复，抽取 `observationRelations.relatedObject` 供回放标签与 Experience 共用，复核关闭。最终无未关闭发现。
- Spec：1 项 P2 上传可空 JsonElement 仍被生成对象类而改写 Result；新增请求红测（4 失败、1 通过），修正 nullable schema 后重生成客户端。
  FactResponse、ObservationSnapshot 和 ObservationUploadRequest 的 10 项任意 JSON 往返测试全通过，复核关闭。最终无未关闭发现。
- `git diff --check` 通过；最终命名检查通过。只保留一项包含实现、测试、审查与本验收记录的 Conventional Commit。

本票实现、自动验证与审查完成，无本票额外人工门禁。与 07 产品维护/本人关联的联合验收按计划由 09 和协调任务承接；
这不是以本票通过替代其他 issue 或生产演练验收。父 PRD 与其他 issue 未修改。
