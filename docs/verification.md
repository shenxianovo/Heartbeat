# 工程验证

命令由 System.CommandLine 统一解析，运行 `./scripts/heartbeat-dev --help` 或任意子命令的 `--help` 查看当前参数。功能分组与执行职责见 [DevCLI README](../tools/Heartbeat.Dev/README.md)。


Heartbeat 使用仓库 Developer CLI 验证三件事：

- `verify`：代码和测试是否通过；
- `quality`：结构质量是否比 Git 基点退化；
- `scenario`：用户场景是否留下可检查证据。

macOS/Linux 使用 `./scripts/heartbeat-dev`，Windows 使用 `scripts/heartbeat-dev.cmd`。仓库目前没有 CI，命令需要人工或 Agent 主动执行。

## 首次准备

```bash
dotnet restore Heartbeat.slnx
npm --prefix src/Frontend/Heartbeat.Web ci
npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts
npm --prefix src/Frontend/Heartbeat.Web exec playwright install chromium
```

Hub 和原生 Collector 另需运行 `./scripts/setup.sh`。

.NET 测试使用 xUnit v3 的 Microsoft.Testing.Platform v2 原生 runner。仓库根 `global.json` 让 .NET 10 的
`dotnet test` 直接运行各测试可执行文件；测试项目不依赖 VSTest adapter 或 `Microsoft.NET.Test.Sdk`。
需要筛选时使用 MTP 参数，例如 `--filter-class`；报告使用 `--report-xunit-trx` 等 xUnit MTP 扩展参数。

## 变更验证

修改前确定比较基点。普通脏工作树使用 `HEAD`：

```bash
./scripts/heartbeat-dev verify changed --base HEAD --plan
./scripts/heartbeat-dev verify changed --base HEAD
./scripts/heartbeat-dev verify full
```

`changed` 按路径选择 .NET、Developer CLI、前端静态检查和 Playwright。无法识别的路径扩为 `full`；重命名同时检查新旧路径。干净工作树必须显式提供 `--base`。

以下契约文档会选择 .NET 测试：

- `docs/protocols/`
- `docs/recording-api.md`
- `docs/recording-storage-model.md`
- `docs/hub-record-delivery.md`

本文件会选择 Developer CLI 测试。其余文档改动没有可执行检查，命令会明确说明。

`.scratch/` 中的临时任务材料保留在变更清单中，但不选择可执行检查，也不触发全仓回退；同批代码改动仍按各自路径选择检查。

选中的检查按顺序执行。某项检查返回普通非零退出码时，保存日志并继续其余检查；结束时汇总已完成检查的名称、退出码和日志位置。全部通过返回 `0`，否则返回第一个失败退出码，后续成功不会覆盖失败。取消（包括检查返回 `130`）立即停止并返回 `130`；进程无法启动、证据无法写入等执行异常仍立即停止。manifest 记录整次运行结果，各项输出保存在对应日志中。此规则只作用于 Dev 检查项之间，不改变 `npm run verify` 等命令内部的失败处理。

## 结构质量

```bash
./scripts/heartbeat-dev quality loc
./scripts/heartbeat-dev quality loc --json
./scripts/heartbeat-dev quality --base HEAD
./scripts/heartbeat-dev quality --base anchor --stock
```

`quality loc` 是当前工作树的只读规模视图，只统计 Git 已跟踪和未忽略文件中的有效行（排除空行与纯注释行），不运行测试、重复检测或复杂度分析，也不创建验证证据。总体按生产、测试、工具、构建、文档和生成代码角色显示；模块与语言按实现代码和测试代码显示，其中实现代码包含产品代码与工具代码。模块以 `src/` 下的产品域目录为准；独立测试项目按其被测产品域显式映射，Developer CLI 作为工具模块单列，未知测试项目显示为 `Unassigned tests`，不猜测归属。

`--base HEAD` 阻止本次改动增加坏味道。`--base anchor --stock` 使用重写锚点 `4e15d57` 查看当前存量；存量模式只报告相对锚点的增量，但仍检查分析完整性和绝对预算。基点中没有 `src/` 生产代码时直接失败。

报告写入 `.artifacts/verification/<run-id>/quality.json`，包含：

- 按生产、测试、工具、构建、生成代码和未归类文件统计的有效 LOC；
- jscpd 检出的生产与测试重复，阈值为 8 行、70 token；
- C# CA1502 与 TypeScript ESLint 检出的函数圈复杂度；
- 复杂度热点占维护负担的 Erosion；
- C# CA1506 类型耦合热点。

C# 指标先构建普通项目，再对 AppKit 与 WinUI 宿主执行托管编译及分析器，避免结构扫描依赖原生链接、签名或 Windows manifest 合并。仍需对应目标框架的 SDK、workload 和已恢复的引用；缺失引用、编译错误或宿主没有分析器输出均失败。此检查不证明原生应用可运行，打包与 UI 行为另行验收。实际命令与临时 solution 保存在证据目录。

闸门规则：

- 新增重复簇失败；
- 函数新跨过复杂度 10，或已超过 10 后继续增长，失败；
- 当前复杂度热点超过 `tools/Heartbeat.Dev/quality-budget.json` 的 `maxProductionComplexityHotspots`，失败；
- 工具缺失、扫描不完整、基点无生产函数或函数定位失败，均失败；
- 未归类文件逐项列出，不归入工具代码；
- LOC、Erosion 和类型耦合只观察，不单独阻断。

存量预算只能下调。确需上调时，必须修改预算文件并说明理由。Domain 不依赖 EF Core 的边界由 `Heartbeat.Domain.Tests/ArchitectureTests` 检查。

## 可复现场景

```bash
./scripts/heartbeat-dev scenario replay-fixture
./scripts/heartbeat-dev scenario hubs-fixture
./scripts/heartbeat-dev scenario delivery
./scripts/heartbeat-dev scenario collector-delivery
./scripts/heartbeat-dev scenario desktop-replay
./scripts/heartbeat-dev scenario native-desktop
./scripts/heartbeat-dev scenario --list
```

| 场景 | 证据 | 不证明 |
| --- | --- | --- |
| `hubs-fixture` | Hub 状态分离、在线启停、离线禁用和通用配置表单 | 认证与 API 是 fixture，不证明真实账号或原生启停 |
| `replay-fixture` | 登录后回放交互、响应式布局和 Chromium 截图 | 认证和 API 是 fixture，不是实际端到端链路 |
| `delivery` | API 与 PostgreSQL 的上传、重放集成测试 | 不覆盖 Web、Hub 或 Collector |
| `collector-delivery` | 真实 macOS Collector 启动、原生快照、Hub 持久接管、后端自动注册与 PostgreSQL 落库、队列清空 | 不覆盖通知、持续采样、物理输入、权限切换、锁屏、休眠或 Web；不独立核对前台应用的具体身份 |
| `desktop-replay` | 本地打包应用 → 真实连接配置/钥匙串 → Mac 原生采集 → 进程内 Hub → 隔离后端 → 同一 Record 的真实 Web 回放 | 需界面操作与 OIDC 登录；不覆盖 Windows、发行签名、公证、自动更新或系统权限交互 |
| `native-desktop` | 临时 Hub、真实 macOS Collector、进程和队列元数据 | 不自动操作权限、锁屏、休眠或物理输入 |

UI、用户流程或 HTTP 展示变更需要相应场景证据。活动泳道性能变更运行[前端基准](../src/Frontend/Heartbeat.Web/README.md#活动泳道拖动基准)；若交互行为也变了，同时运行 `replay-fixture`。基准由前端脚本保存报告，退出码只说明测量是否完成，不证明性能达标。原生场景默认不保存用户内容；只有显式使用 `--include-sensitive-evidence` 才保存 Collector 日志。`--keep-environment-on-failure` 会在失败时保留独立环境。

### 组合实现来验证行为

测试范围由要验证的行为和实际接口决定，项目目录不定义测试层级。接管实现可以单独验证，也可以与注册、上传及存储实现连接后验证；组合测试直接连接生产实现，不把若干独立测试顺序执行当作组合验证。责任划分见 [ADR-0013](adr/ADR-0013-scenarios-compose-implementations.md)。

例如，`RecordOutboxTests` 通过接管接口和真实 SQLite 验证持久性，`HubDeliveryTests` 将同一接管实现与上传实现、HTTP 后端和 PostgreSQL 连接起来验证交付。跨进程场景进一步使用实际宿主和原生读数。不同范围复用实现、依赖设施与契约，不要求每份断言在所有范围原样运行。

Developer CLI 的场景使用以下小型设施，按需直接组合：

| 设施 | 提供的能力 |
| --- | --- |
| `ScenarioEnvironment` | 独立 Compose 项目、端口与数据卷；按需启动依赖、迁移、查询数据库；统一清理和证据生命周期 |
| `ScenarioProcess` | 真实子进程的启动、输出排空、等待退出、停止和作用域清理，可脱离 Docker 单独使用 |
| `ScenarioCollector` | 构建并启动生产 Collector，将 Hub 地址和独立 Target 接入进程，支持单次与持续采集 |
| `ScenarioWait`、`HubQueueStatus` | 有截止时间的状态等待和 Hub HTTP 状态读数 |

`native-desktop` 组合 Hub、持续采集和接管断言；`collector-delivery` 在同样设施上组合数据库、迁移、API 和落库断言。前者等到首次接管才进入人工引导，结束输入关闭或进程提前退出均失败。场景采样间隔为 1 秒，使用独立 Target，不复用本地开发 Collector 的身份。

新增行为时，优先扩展现有场景的动作与断言，复用已有运行设施；只有存在独立验收目的才增加场景入口。网络或认证等条件属于具体测试的输入，不为条件与依赖的每种排列增加命令。生产行为和协议断言留在对应测试或场景，运行设施不解释这些行为。这里不建立任意依赖图或通用场景 DSL。

### Collector 到落库

采集启动、Hub 接管、注册或上传链路变化时运行 `scenario collector-delivery`。需要已登录的 macOS 桌面、Docker、`.env.local` 中有效的 `HEARTBEAT_OWNER_ID` 与 `HEARTBEAT_API_KEY`，以及可访问的 Auth 服务；测试仍走真实令牌交换和 API 认证。

场景先创建独立 Compose 项目、临时 PostgreSQL/SQLite 数据卷和 Hub 端口，不发布 API 或数据库端口。空库迁移完成后，API 暂不启动，真实 Collector 用独立 Target 执行 `--once`；场景确认 Collector 成功退出且 Hub 有待交付项，再启动 API。测试不手工提交 Record，也不预注册 Timeline、Collector 或 Track。

完成条件是接管数量与落库数量一致、Owner/key/Target 绑定正确、Record 在本次采集时间窗内且载荷 Target 正确、存在符合前台应用协议的 Record、Hub pending/failed 均为零。只产生能力状态不能通过。等待实际状态有超时，不靠固定休眠宣告成功。

`custody.json` 保存采集时间与 Hub 接管数量，`delivery.json` 保存最后一次数据库检查和队列状态，`database-check.sql` 保存实际检查语句；构建、环境启动和清理日志保存在同次证据目录。默认不导出原生载荷或服务运行日志，`--include-sensitive-evidence` 才保存 Collector 输出。完成或失败后清理独立环境；`--keep-environment-on-failure` 显式保留失败环境，项目名见 manifest。清理未确认也视为失败。开发环境及其数据卷不参与本场景。

### 桌面应用到真实 Web 回放

```bash
./scripts/heartbeat-dev scenario desktop-replay
```

场景按[客户端 README](../src/Desktop/README.md)生成 ad-hoc 签名的 Mac 应用包，在临时 profile 中启动，并连接本轮隔离 PostgreSQL、API 和生产 Web。依赖有效 Auth、已安装的前端依赖与 Playwright Chromium，以及 Auth 允许的 `http://localhost:3000/auth/callback`。按命令输出填写连接和 API key、保存并开始采集；凭据只写临时 profile 对应的钥匙串条目。

保持真实 `Heartbeat Dev` 窗口在前台，直到下一步提示。场景从生成的应用包读取身份，等待其自身的应用 Record 落库，并核对 Owner、Target、采集时间窗和至少两秒的持续区间；不再编译额外的白板测试 app。随后按提示暂停采集，等队列排空，再从菜单栏退出；场景核对退出码、实际 SQLite 队列和同一 Record 的最终区间。它验收本地应用包启动，不替代拖入 Applications 的人工安装验收。

接着打开独立 Chromium，点击真实登录入口。请在五分钟内使用 `.env.local` 中 Hub Owner 对应的账号完成登录。浏览器通过自身连接映射把 `localhost:3000` 指向本次随机 Web 端口，保留已注册的 OIDC origin；不会占用或停止开发环境的 3000 端口，不拦截或伪造 HTTP 响应，不注入登录 token。浏览器上下文为临时会话，结束后关闭。

浏览器验收固定 UTC 时区，使用本次 Record 的实际时间范围查询，不依赖运行时仍处于采集当天。验收核对真实 Track 响应中的 Collector 绑定、真实 Record 响应中的 ID/时间/应用值，然后点击泳道并展开详情，核对同一 Record ID、应用身份、Target 和展示时间。只落库、只返回 API 响应或只出现一个应用名称都不足以通过。

`desktop-setup.json` 只保存非秘密的连接和临时 profile 路径，`desktop-custody.json` 保存退出后队列状态，`replay-expectation.json` 保存最终 Record 的数据库证据，`replay.json` 保存浏览器阶段与结果，`replay-record.png` 只截取已核对的客户端应用详情。默认不保存其他原生载荷、登录截图、浏览器会话或网络 trace。

结束时清理临时 profile 和专属钥匙串条目，不保留其数据库、原生载荷或凭据；清理失败不能作为成功。失败和取消同样清理隔离环境；显式 `--keep-environment-on-failure` 可保留 Docker 环境，但不保留浏览器登录凭据。此场景不复用用户正常客户端数据目录。

## 现场探针

窗口标题静置参数必须根据真实读数判断：

```bash
./scripts/heartbeat-dev probe window-title --duration-seconds 120
./scripts/heartbeat-dev probe window-title --readings <path> --dwell-seconds 1,1.5,2
./scripts/heartbeat-dev probe window-title --from-database --since <time> --until <time>
```

探针默认只保存时间、应用身份、标题长度、指纹和相邻标题形态；显式启用敏感证据才保存标题原文。读不到 Accessibility 标题时以非零退出，不把无效读数解释为稳定。

`--from-database` 只用于无法取得原生权限或需要长时间样本的情况。数据库 Record 已经过当前投影规则处理，不能证明原生通知是否漏送。

生产规则位于 `DesktopRecordProjector.ObserveWindowTitle` 和 `SettlePendingTitle`。探针模拟通过 `tests/window-title-dwell-scenarios.json` 及两侧 reconciliation tests 与生产规则对账；模拟结果不是协议权威。

## 证据管理

每次 `verify`、`quality`、`scenario` 和 `probe` 都在 `.artifacts/verification/<run-id>/` 写 `manifest.json`，记录命令、时间、退出码、由退出码派生的 `status`（`succeeded`、`failed`、`cancelled`）、产物、敏感证据标志和限制。取消操作使用退出码 130；强制杀进程、断电和产物目录不可写不在保证范围内。前端性能基准的报告独立保存在前端 `.artifacts/perf/`，不写入 CLI manifest。

```bash
./scripts/heartbeat-dev artifacts list
./scripts/heartbeat-dev artifacts prune
./scripts/heartbeat-dev artifacts prune --apply
./scripts/heartbeat-dev artifacts inventory-local
```

`prune` 默认只预览，`--apply` 才删除验证运行；不删除 `.artifacts/quality-baselines/`。默认保留最近 10 次、最近 5 次失败和 2 天内的运行。没有 manifest 的运行按失败保留。

`inventory-local` 只清点旧 `.local`，从不删除。Web 检查在每次运行的隔离工作区中执行，共享已安装依赖；运行期间不要修改共享依赖。成功运行会删除临时工作区，保留日志和浏览器报告；失败运行保留工作区供排查，随后仍受上述证据保留规则管理。

## 结论边界

- 工具只报告测试、量化指标和场景证据，不判断架构是否合理。
- `collector-delivery`、`desktop-replay` 和 `native-desktop` 运行真实进程；其余 scenario 是带证据的测试子集。
- 质量结论只对指定 Git 基点和仓库存量预算成立。
- fixture、mock 或局部集成结果不能描述成真实端到端验证。
- 最终报告必须列出检查项、结果、证据路径和未覆盖范围。

路由、用户入口、稳定 selector、renderer 或场景命令变化时，同步更新验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。
