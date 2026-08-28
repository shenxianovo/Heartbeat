# 02 — Owner 逐层走查仓库文档

Status: done

## 走查顺序

1. **仓库/文件夹级**：`README.md` → `CONTEXT-MAP.md` → `docs/architecture/system-overview.md` → `docs/development.md` → runbook index，确认当前产品边界、目录责任与日常入口。
2. **项目级**：按 `Heartbeat.slnx` 和两个 `package.json` 逐项核对 executable、library、测试项目、部署产物与 owner；判断哪些项目需要 README，哪些只需由上层地图指向。
3. **模块级**：优先走查 Collector Protocol Client、Collector Runtime、Headless Instance Pipelines、System InProcess adapter、Browser Delivery、Analytics ingest/projection、Dashboard 数据 adapter；为每个深模块确认 interface、内部秘密、失败语义和测试入口。
4. **历史层**：对照 `docs/architecture/compatibility-debt.md`，逐条裁决继续支持、计划移除或转为稳定边界。

## 完成标准

- [x] 每份现有架构/开发/项目文档都被标记为保留、更新、合并或删除，且理由明确。
- [x] 历史 ADR 中指向已退役源文件的链接被标记为历史快照或改为当前替代入口，不让 broken link 冒充现行实现。
- [x] 复核 collector-plugin-runtime issue 10 的对账结果：Collector Protocol / Fact Model `Draft 0.2` 已明确裁决为定稿规范、继续演进的 draft，或拆分后的 implemented profile / future design。
- [x] 文件夹责任与实际项目清单一致；同一事实只有一个权威来源，其余文档只链接不复制。
- [x] 每个关键深模块都有可发现的接口说明与最小验证入口，不要求为浅模块补 README。
- [x] 走查中形成的术语修正立即同步相关 `CONTEXT.md`；满足 ADR 门槛的真实决策才建 ADR。
- [x] 走查结果回写本 issue，并把后续改动拆成有 owner、验收与优先级的 issue。

## Documentation disposition

| 文档 | 处置 | 理由 |
| --- | --- | --- |
| `README.md`、`CONTEXT-MAP.md` | 保留并更新 | 分别承担门面/导航与产品定位/上下文地图；不复制精确项目清单 |
| `collection/CONTEXT.md`、`server/CONTEXT.md`、`frontend/CONTEXT.md`、`shared/CONTEXT.md` | 保留；相关术语就地更新 | 领域词汇权威，不承载实现清单 |
| `docs/architecture/system-overview.md` | 保留 | 当前运行拓扑与跨实现契约地图的唯一架构入口 |
| `docs/architecture/compatibility-debt.md` | 保留并更新 | 真实落盘/客户端兼容债账本，已补 owner 裁决与退出原则 |
| `docs/development.md` | 保留并大幅收窄 | 只留日常启动、验证与停止路径；低频操作下沉 Runbook |
| `docs/runbooks/README.md` 与现有 Runbook | 保留 | 低频、高风险操作按任务发现，不并回 Development Guide |
| `docs/api.md`、`docs/db.md` | 保留；数据库导读吸收旧 Server EF 便签 | 解释调用方/设计意图，endpoint/schema 仍链接 OpenAPI、实体与迁移 |
| `collection/contracts/README.md`、`collection/protocol/conformance/README.md` | 保留 | 分别是 Fact payload 与跨语言协议行为的权威入口 |
| Browser、Headless 项目 README | 保留并收窄 | 从长篇规范改为职责、目录、失败不变量、验证与制品归属索引 |
| Server 项目 README | 保留并替换 | 旧两行 EF 便签合并到 `docs/db.md`，README 改为 Analytics 目录地图 |
| Protocol、Hub、System、VRChat、Frontend 项目 README | 新建 | 深模块或独立制品需要可发现的 interface、验证与权威链接 |
| Desktop UI/Updater/Windows/Mac、Core、Reference fixture、所有 Tests | 不新建 | 浅 library、composition head 或相邻测试由上层地图/生产 README 指向 |
| 历史 ADR | 保留并修正导航 | 现行决策指当前实现；已删除源码标历史路径并链接 successor ADR |

本轮不删除任何文档文件；只删除被权威入口替代的重复或过期内容。

## Comments

- 2026-08-28，仓库/文件夹级走查第一轮：owner 将产品定位确认为“以桌面活动为核心的个人
  数字活动档案与回放系统”；确认 README 负责定位与导航、Context 负责领域边界与术语、
  system overview 负责当前拓扑、ADR 负责决策理由、schema/conformance/validation code
  负责可执行契约、Development/Runbook 分别负责日常与低频操作；确认 Dashboard 是回顾与
  管理入口，以展示为主，同时把叙事知识写回 Analytics，并直连 Hub 完成交互授权。
- 2026-08-28，仓库/文件夹级走查第二轮：owner 确认 README、Context Map、system overview、
  Development Guide 与 Runbook index 各自保留，不合并；README 只维护稳定的生产目录责任，
  精确项目清单留给 solution/package manifest；Development Guide 收紧为近乎病态简洁的日常
  路径，`dotnet test` 是完整 .NET 测试入口，Browser 测试单列。再次确认 Hub Instance 是运行
  宿主而非 Subject：无头 Hub 上的账号采集事实归 Account Subject，不归服务器 Device。
- 2026-08-28，项目级走查：owner 不要求记录具体人名，以 bounded context 和项目/制品责任
  表达 owner；项目 README 保持简短目录性质，只在独立运行/部署或深模块需要可发现 seam 时
  建立。当前 Browser Package 随 Desktop release、VRChat Package 随 Headless image 交付；
  Package 托管与下载是明确计划但尚未实现的 future design，不写成 current state。全仓验证
  入口分为 .NET、Browser、Frontend 与 Collector Contracts 四块。
- 2026-08-28，Protocol Client / Collector Runtime 走查：owner 确认三种 Transport Binding
  只改变承载，不改变 ACK、重试、Gap 或生命周期语义；Registry 下载/验证失败不改写 Desired
  State，旧已验证 Activation 继续运行；正式 Registry 中同一 PackageId + Version 内容不可变；
  Browser 下载或 staged 完成不等于更新成功，须等待精确新制品 Activation Ready；新 PackageId
  不隐式继承旧 Instance、配置、Secret 或 Stream。宿主升级默认在全 Instance 兼容预检失败时
  暂停，但 owner 可以明确停用不兼容 Collector 后继续升级。以上 future Registry 约束与现有
  collector-plugin-runtime PRD 的 Package Registry、锁定状态、更新事务与兼容场景一致。
- 2026-08-28，Headless/System/Browser 深模块走查：owner 确认 Headless 投影必须显式携带
  Collector Instance/Subject context，不根据 Source 或配置猜测；各 Instance 的上传、缓存和
  故障域隔离，单账号失败不暂停 Fleet。system 原生回调只入队，输入洪峰时不阻塞 hook、不
  驱逐已接收事件，而是丢当前新事件并报告 Gap。Browser ACK 必须同时匹配 FactId 与 Revision，
  旧 Revision ACK 不删除新 Revision；停用只停止新观察与发送，不清空未 ACK outbox。
- 2026-08-28，Analytics/Dashboard 走查：owner 确认 mixed ingest batch 的 contract 错误应整批
  零副作用返回 4xx，由 UploadStream 二分并 dead-letter poison fact；坏时间或 Segment identity
  冲突不得继续 `skip + 200`。报表时长只统计 system Source；Recap GET 保持只读，生成走显式
  POST；Dashboard 内部保持 Analytics 与 Hub 两条 adapter 边界。曾讨论把 Recap 生成改成脱离
  SSE 的系统任务，owner 决定暂缓，继续采用 ADR-042 已交付的连接绑定语义：离开页面即取消，
  中途不缓存，last-good 保留。
- 2026-08-28，历史层走查：owner 接受 collector-plugin-runtime issue 10 的既有裁决——两份
  Draft 0.2 是已删除的一次性推导工具，长期事实已拆入 implemented profile 与 future design，
  不复活 Draft。兼容债按真实安装/落盘迁移事件退出；Analytics 目标为原生 Subject + Fact/Stream
  ingest；AppIdentity 双写/别名在客户端与数据库审计后删除；旧 source registry 最终收窄为
  declaration seam。11 个历史 ADR broken links 已按“现行决策指当前实现、退役实现标历史路径并
  指 successor ADR”的规则修复。
- 2026-08-28，closeout：后续优先级确认为 P1 strict Segment ingest、P2 compatibility debt
  retirement、P2 Package Registry/Delivery 设计；分别由 issue 05、03、06 承接。issue 02 的
  文档处置、broken links、Draft 对账、项目地图、深模块发现性、术语同步与后续拆单验收全部完成。
