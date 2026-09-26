# 工程验证

Heartbeat 通过 Developer CLI 执行三类验证：`verify` 检查代码和测试，`quality` 检查相对 Git 基点的结构退化，`scenario` 为用户场景保存可检查证据。统一入口为：

```bash
dotnet run --project tools/Heartbeat.Dev -- <子命令>
```

用 `--help` 查看完整参数。仓库没有 CI，需要开发者或 Agent 主动执行。

## 首次准备

```bash
dotnet restore Heartbeat.slnx
npm --prefix src/Frontend/Heartbeat.Web ci
npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts
npm --prefix src/Frontend/Heartbeat.Web exec playwright install chromium
```

Hub 和原生 Collector 场景还需要运行 `dotnet run --project tools/Heartbeat.Dev -- env setup`。.NET 测试使用 xUnit v3 与 Microsoft.Testing.Platform v2；筛选和报告参数使用 MTP / xUnit MTP 语法。

## 变更验证

任务收口使用一个显式基点组合变更验证和结构质量检查。普通脏工作树用 `HEAD`，分支任务用任务开始的提交或 merge base：

```bash
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD --plan
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD
```

`closeout` 先将基点固定为一个提交，再顺序执行 `verify changed` 的检查计划与 `quality` 的结构闸门，共享同一证据目录。`closeout.json` 保存基点、计划和两部分退出码；普通验证失败仍继续质量检查，取消或执行异常停止。计划会明确提示场景、原生验收和性能基准仍需按改动另行选择。职责见 [ADR-0023](adr/ADR-0023-quality-classification-and-closeout.md)。

开发过程中仍可单独运行：

```bash
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --plan
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD
dotnet run --project tools/Heartbeat.Dev -- verify full
```

`changed` 按路径选择 .NET、Developer CLI、前端静态检查和 Playwright；无法识别的路径扩为 `full`。干净工作树必须显式提供比较基点。契约文档会选择相应测试，其他纯文档改动可能没有可执行检查；`--plan` 的输出是本次选择的权威说明。

`verify full` 包含全部常规代码与测试检查，不包含 `quality` 或场景验收。.NET 架构测试约束 Domain 不依赖其他 Heartbeat 层，以及 Backend、Hub 和 Contracts 不直接引用可选 Collector。

各检查日志和汇总写入同一次验证目录。普通失败后继续其他检查；取消立即停止并返回 `130`；进程无法启动或证据无法写入时立即失败。

## 结构质量

```bash
dotnet run --project tools/Heartbeat.Dev -- quality loc
dotnet run --project tools/Heartbeat.Dev -- quality --base HEAD
dotnet run --project tools/Heartbeat.Dev -- quality --base anchor --stock
```

`quality loc` 只统计工作树有效行，不生成验证证据。`quality --base` 检查重复、函数复杂度、分析完整性和绝对预算，并把报告写入 `.artifacts/verification/<run-id>/quality.json`。

源码角色由 `SourceCorpus` 统一定义，重复扫描按生产/测试角色分别进行；前端 `.test.*`、`.spec.*` 和测试目录中的代码属于测试。jscpd 固定使用 strict 模式、最小 8 行和 70 tokens，使用基线与工作树的路径并集进行同口径比较。生成代码及其他角色不进入这两类扫描，扫描配置保存在证据目录，便于核对文件范围。

闸门规则：

- 新增重复簇失败；
- 函数新跨过复杂度 10，或超过 10 后继续增长，失败；
- 复杂度热点超过 `tools/Heartbeat.Dev/quality-budget.json` 的预算，失败；
- 工具缺失、扫描不完整、基点无生产函数或函数定位失败，失败；
- LOC、Erosion 和类型耦合只观察，不单独阻断。

存量预算统计 C# 与 TypeScript 生产函数的热点总数。预算只能下调；确需上调时修改预算文件并说明理由。`--stock` 跳过相对基点的新增重复与复杂度退化闸门，仍要求扫描完整且存量预算通过。C# 分析会编译普通项目以及 AppKit / WinUI 宿主的托管部分，但不证明原生应用能够打包或运行。

## 可复现场景

```bash
dotnet run --project tools/Heartbeat.Dev -- scenario --list
dotnet run --project tools/Heartbeat.Dev -- scenario replay-fixture
dotnet run --project tools/Heartbeat.Dev -- scenario hubs-fixture
dotnet run --project tools/Heartbeat.Dev -- scenario delivery
dotnet run --project tools/Heartbeat.Dev -- scenario collector-delivery
dotnet run --project tools/Heartbeat.Dev -- scenario runtime-replay
dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay --foreground
dotnet run --project tools/Heartbeat.Dev -- scenario native-desktop
```

| 场景 | 证明 | 不证明 |
| --- | --- | --- |
| `hubs-fixture` | Hub 状态分离、在线启停、离线禁用和配置表单 | 真实认证、API 或原生启停 |
| `replay-fixture` | 回放交互、响应式布局和 Chromium 截图 | 真实认证与端到端链路 |
| `delivery` | API 与 PostgreSQL 的上传、重放集成 | Web、Hub 或 Collector |
| `collector-delivery` | 真实 macOS 单次采集、Hub 接管、后端注册、落库和队列清空 | 持续采样、物理输入、权限、锁屏、休眠或 Web |
| `runtime-replay` | 后台真实 DesktopRuntime、Collector 投影、Hub、Auth、后端和无头 Web，默认离线接管、强杀与恢复回放 | 系统观测受控；不证明原生 UI、OS 采集、权限或系统凭据 |
| `desktop-replay --foreground` | 本地开发应用、真实 Auth、原生采集、进程内 Hub、隔离后端与同一 Record 的 Web 回放 | 普通构建 Keychain、Windows、发行或系统权限交互 |
| `native-desktop` | 临时 Hub、真实 macOS Collector、进程和队列元数据 | 权限、锁屏、休眠或物理输入 |

UI、用户流程或 HTTP 展示变更运行相应场景。日常业务链路优先 `runtime-replay`；原生 UI 场景会占用桌面，必须显式安排后使用 `--foreground`，不因一般收发或图表变化默认运行。活动泳道性能变更运行[前端生产基准](../src/Frontend/Heartbeat.Web/README.md#活动泳道拖动基准)；若交互行为也变化，同时运行 `replay-fixture`。原生场景默认不保存用户内容，只有显式使用 `--include-sensitive-evidence` 才保存 Collector 日志。

### 组合实现来验证行为

场景按验收目的直接组合生产实现与小型运行设施；顺序执行多个独立测试不等于验证组件之间的真实交互。新增行为优先扩展现有场景，只有出现独立验收目的时才增加入口。责任决策见 [ADR-0013](adr/ADR-0013-scenarios-compose-implementations.md)。

### Collector 到落库

采集启动、Hub 接管、注册或上传链路变化时运行 `scenario collector-delivery`。它需要已登录的 macOS 桌面、Docker，以及 `.env.local` 中有效的 Owner 与 API key。

场景使用独立 Compose 项目、临时 PostgreSQL/SQLite 和独立 Target。真实 Collector 在 API 尚未启动时执行一次采集，Hub 持久接管后再启动 API；随后核对 Owner、Target、采集时间窗、前台应用 Record、落库数量和空队列。场景不手工提交 Record，也不预注册资源。

### 日常后台回归

`scenario runtime-replay` 是桌面业务链路、Hub 收发及恢复的日常入口。它不启动 AppKit/WinUI、不请求系统观测权限、不调用 UI 自动化；运行时通过生产 `ConfigureAsync/StartAsync` 接口配置和启动，浏览器始终无头，使用真实 Auth 签发的短期会话。

后台只替换 OS 观测与临时凭据适配，后续 Collector 投影、SQLite 接管、HTTP 交付、PostgreSQL 和 Web 都是真实实现。与原生入口共用阶段与 Record 对账，默认包含正常回放、停止 API、离线接管、强杀运行时进程、从保存的配置及凭据重启、恢复交付和 Web 回放。`services-and-runtime`、`configure-runtime` 阶段明确区别于原生打包和首次 UI 配置，采集诊断中的 foreground 为 null，不声称读过系统前台。

`--keep-environment-on-failure` 可保留失败的 Compose 环境；运行时子进程和临时 profile 始终清理。后台入口不接受 `--foreground`、`--interactive-login` 或原生日志开关。首次设置、原生界面和系统能力另行验收，见 [ADR-0025](adr/ADR-0025-background-and-native-acceptance.md)。

### 桌面应用到真实 Web 回放

`scenario desktop-replay --foreground` 从本地打包的 Heartbeat Dev 开始，使用空的临时 profile，通过真实原生 UI 配置连接、保存凭据并开始采集；暂停后等待交付完成，在真实 Web 时间线选择同一 Record 并核对详情。Collector、进程内 Hub、Auth、隔离 PostgreSQL/API 和生产 Web 均为真实实现。

需要已解锁的 macOS 桌面、Docker、有效 `.env.local`，以及运行终端/代理的辅助功能与 System Events 自动化权限。默认用真实 Auth 签发的短期令牌建立临时浏览器会话；`--interactive-login` 改为真实 OIDC 人工登录。前者不证明 OIDC 跳转流程。

`--recovery` 在正常回放后停止隔离 API，再次采集并暂停，使用 SQLite 在线备份读取 Hub 已接管快照；强制退出应用，重启并确认保存的凭据及 Hub 身份恢复，恢复 API 后逐条核对接管记录的 ID、Owner、路由、内容与时间。允许重启后新产生的记录，但必须一同完整落库；队列清空不能替代对账。最后验证恢复记录的真实 API 和 Web 回放。接管前仍在 Collector 内存中的观测不在此恢复承诺内。

步骤在具体代码中组合，打包、UI 操作、接管快照、数据对账和浏览器回放各自维护对应边界。Compose 继续维护服务启动依赖。

`journey.json` 保存阶段标识、状态、已完成步骤和各阶段产物目录。`first-collection/collection.json` 与 `offline-custody/collection.json` 持续记录采样时间、受控进程是否退出/位于前台、队列 pending/failed，以及 Record 对 Owner、Collector、Target、Track、payload 结构、应用身份、时间窗和时长的逐级匹配计数。探针失败记录异常类型，该项值不可视为有效读数；这些数据在超时清理前保存。前台状态是离散采样，不能证明两次采样之间未发生切换；跨进程探针也不是同一事务快照，只用于定位，不替代暂停后的接管对账。

正常和恢复回放通过泳道键盘入口选择同一 Record，并检查该 Desktop 的真实 Hub 活动上报、Web 曲线、窗口汇总及 `hub-activity.png`（仅聚合活动，不含机器身份或 Record 内容）；静态截图不证明动效时序，动效另由 `hubs-fixture` 检查。原生驱动按已核对 PID 的稳定进程 ID 和应用窗口标题定位，允许其他同名构建同时运行。点击开始后，验收驱动主动将受控窗口保持前台三秒，以产生至少两秒的前台观测见证；仍按真实记录对账，不放宽验收时长。

正常和恢复回放各自保存 `normal-web-replay/` 与 `recovered-web-replay/` 下的报告、浏览器日志及仅含时间的截图，失败时也不覆盖前一阶段。接管快照元数据在 `offline-custody/`、`forced-exit-and-restart/`，对账结果在 `delivery-recovery/reconciliation.json`。场景用 `DesktopStage` 标识阶段，用 `DesktopReplayBatch` 传递记录集合和见证，浏览器接收显式产物路径；正常及交互 OIDC 共用回放验证脚本。原始记录仅在进程内或临时环境变量中传递，不写入证据文件。

不导出 API key、短期令牌、其他应用身份、原始原生 payload 或 profile。临时凭据、浏览器会话和隔离环境结束时清理，`--keep-environment-on-failure` 只保留失败的 Docker 环境。下载分发、安装器、普通 Keychain、Windows、权限授权、锁屏休眠与升级仍需独立验收。

## 现场探针

窗口标题静置参数使用真实读数复核：

```bash
dotnet run --project tools/Heartbeat.Dev -- probe window-title --duration-seconds 120
dotnet run --project tools/Heartbeat.Dev -- probe window-title --readings <path> --dwell-seconds 1,1.5,2
dotnet run --project tools/Heartbeat.Dev -- probe window-title --from-database --since <time> --until <time>
```

探针默认只保存时间、应用身份、标题长度、指纹和相邻形态；显式启用敏感证据才保存标题原文。数据库 Record 已经过当前投影规则处理，不能证明原生通知没有漏送。

## 证据管理

`verify`、`quality`、`scenario` 和 `probe` 在 `.artifacts/verification/<run-id>/` 写入 `manifest.json`，记录命令、时间、退出码、产物、敏感证据标志和限制。

```bash
dotnet run --project tools/Heartbeat.Dev -- artifacts list
dotnet run --project tools/Heartbeat.Dev -- artifacts prune
dotnet run --project tools/Heartbeat.Dev -- artifacts prune --apply
dotnet run --project tools/Heartbeat.Dev -- artifacts inventory-local
```

`prune` 默认只预览，`--apply` 才删除；默认保留最近 10 次、最近 5 次失败和两天内的运行。`inventory-local` 只清点旧 `.local`，不删除内容。

## 结论边界

- 工具报告测试、量化指标和场景证据，不判断架构是否合理。
- fixture、mock 或局部集成结果不能描述成真实端到端验证。
- 质量结论只对指定 Git 基点和当前预算成立。
- 最终报告列出检查项、结果、证据路径和未覆盖范围。

路由、用户入口、稳定 selector、renderer 或场景命令变化时，同步更新验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。
