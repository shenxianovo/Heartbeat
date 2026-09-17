# 工程验证

Heartbeat 把“测试通过”“结构没有继续腐蚀”“用户场景有可检查证据”分成三层验证。统一入口是 .NET 10 Developer CLI：macOS/Linux 运行 `./scripts/heartbeat-dev`，Windows 运行 `scripts/heartbeat-dev.cmd`。自己启动开发环境和 Agent 自验证使用同一套实现。

三层里只有第一层是自动判定的：第二层给数字并对“新增的坏味道”挂人，第三层里只有 `native-desktop` 真的跑起进程和数据库，其余两个场景是复跑第一层的一个子集并留下证据（见[可复现场景](#可复现场景)）。没有 CI：这些命令谁运行才谁跑，仓库里没有替你跑它们的流水线。

## 首次准备

```bash
dotnet restore Heartbeat.slnx
npm --prefix src/Frontend/Heartbeat.Web ci
npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts
npm --prefix src/Frontend/Heartbeat.Web exec playwright install chromium
```

Hub 和原生 Collector 仍使用 `./scripts/setup.sh` 创建 `.env.local`。`setup.sh` 本阶段不合入 Developer CLI。

## 变更验证

开始修改前记录比较基点；有未提交变更时默认基点是 `HEAD`：

```bash
./scripts/heartbeat-dev verify changed --base HEAD --plan
./scripts/heartbeat-dev verify changed --base HEAD
./scripts/heartbeat-dev verify full
```

`changed` 根据集中维护的路径映射选择 .NET、Developer CLI、前端静态检查和 Playwright。重命名同时考虑旧路径删除和新路径新增，避免跨目录移动后漏掉源项目检查。无法识别的路径自动扩为 `full`，而不是猜测可以少跑。干净工作树必须显式给出 `--base`；脏工作树省略时才默认 `HEAD`。

契约文档不是散文：`docs/protocols/` 下的协议、`docs/recording-api.md`、`docs/recording-storage-model.md`、`docs/hub-record-delivery.md` 写的是后端与采集端都要遵守的语义，改动它们会选中 .NET 测试；`docs/verification.md`（本文件）改动会选中 Developer CLI 测试。其余散文（`docs/` 其他文件、`README.md`、`AGENTS.md`、`CONTEXT.md`）不选任何检查，并且会明说“没有可执行的检查”，而不是假装跑过什么。

## 结构质量

```bash
./scripts/heartbeat-dev quality --base HEAD              # 闸门：拦这次改动带来的新增
./scripts/heartbeat-dev quality --base anchor --stock    # 存量：量重写以来累积了多少
```

### 基点

`quality` 只有一种基点语义：拿工作树和某个 Git 提交比。`--base HEAD` 是给一次改动做闸门用的；`--base anchor` 解析成重写谱系锚点 `4e15d57`（`feat(recording): implement timeline storage model`），它是净室重写里第一个具备完整结构（`Heartbeat.slnx` + `src/` + `tests/` + `docs/`）的提交，在它之前只有空提交和 agent skills。

重写之前的 `main` 不是可用基点：那棵树把源码放在 `collection/`、`server/`、`shared/`、`frontend/` 下，而本仓的生产语料是 `src/`。这种基点以前会一路扫完然后给出“生产 LOC 0、复杂度无生产函数、所有重复都是新增”，看起来像检查通过或全线崩塌，实际只是基点选错了。现在 `quality` 先判断基点：基线里 `src/` 下没有一个生产文件时，什么都不扫，直接以非零退出，并说明这棵树的代码实际在哪些顶层目录、以及该用哪条命令（存量用 `--base anchor --stock`，闸门用改动的起点提交）。

`--stock` 只改变“谁挂人”：相对基点的新增重复与新增复杂度只报数不拦，因为把重写以来的全部积累当成“这次改动引入的”没有意义；扫不出来（工具缺失、分析器没产出生产函数）和突破存量预算这两类仍然拦。

跨基点扫描需要在基线提交上重新 restore 和 `npm ci`，所以基线工作树按提交缓存在 `.artifacts/quality-baselines/<commit>`，同一基点第二次起直接复用（本机第二次跑锚点约 30 秒）。

### 报告内容

质量报告写入 `.artifacts/verification/<run-id>/quality.json`，包含：

- 生产代码有效 LOC 的基线、当前值和语言增量。归类规则是显式的、有顺序的：生成物 → 测试 → 文档 → 构建描述 → 生产根（`src/`）→ 工具根（`tools/`、`scripts/`、`.agents/`、`.config/`、`.github/`）→ 未归类。构建与环境描述（`*.csproj`、`*.props`、`*.slnx`、`Dockerfile`、compose 与 workflow 的 YAML、前端 `*.config.*`）单独计入 build，不算生产语料；锁文件、生成代码、EF Designer 和 Model Snapshot 不计入生产语料，Initial migration 保留。认得出语言但不在任何已声明的根下的文件归“未归类”，按路径逐个列出来，不再兜底记成 tooling——兜底的代价是有人把代码放错地方而没人看得见。
- jscpd 精确重复，生产代码与测试代码分开扫，两者都是闸门。strict 模式、至少 8 行 70 token，用 `--baseline-from-ref` 和基点比，只有相对基点新增的重复簇才失败；失败信息给出两段代码的文件与行区间、行数、token 数，以及是新增还是存量。测试之间的重复同样算：一个类型改名要在八个地方跟着改，就是它带来的。这里不再生成“只观察不闸门”的近似重复报告——那份报告没有人消费。
- 圈复杂度增量与存量上限。C# 使用 CA1502，TypeScript 使用 ESLint `complexity`，报告复杂度大于 10 的生产函数。闸门模式拦新跨过 10 或已经高于 10 后继续增长的函数；同时 `tools/Heartbeat.Dev/quality-budget.json` 提交了一个与基点无关的绝对上限 `maxProductionComplexityHotspots`，每次运行都对当前值校验。这个数字只准往下改：往上改要动这个文件、出现在 diff 里、并在文件里写清理由。基线分析器量不到任何生产函数时报“基点不可用”，不报 0。
- Erosion 是“复杂度 × 函数有效行数”的维护负担中，由复杂度大于 10 的函数贡献的比例；报告基线、当前值与增量，只作观察，不单独阻止提交。
- C# 类型耦合观察。SDK 内置 Roslyn Analyzer 的 CA1506 报告引用类型过多的类型／成员，沿用其默认阈值（类型 95、其他符号 40）。`complexity.coupling` 包含基线与当前生产代码热点；只作观察，不影响质量闸门。普通构建继续使用 `Directory.Build.props` 的推荐规则，扫描专用规则只在 `quality` 构建中开启。

C# 函数范围由 Roslyn 语法树识别，有效行按语法 token 覆盖的行计数，不再通过分号和大括号猜测边界。主构造函数计声明及字段／属性初始化器，顶层入口计顶层语句；内嵌函数的源码仍属于外层函数范围。C# 函数身份包含命名空间、类型、成员和参数类型，TypeScript 使用语法作用域和声明名称；诊断按行列位置匹配，区分同一行的多个函数。没有名称的回调通过所属作用域内的顺序区分：插入或重排这类回调可能改变匹配，重命名或移动文件也视为新的函数身份。

C# 构建诊断固定为英文并保留在 `baseline-csharp.log`、`current-csharp.log`。TypeScript 也保留 ESLint 的隐式函数口径，单独测量类字段初始化器和静态块。分析失败、函数定位失败或 TypeScript 函数未完整测量都会使检查失败；不会用复杂度 1 静默填补漏测。

这些指标用于观察本项目趋势。重复行比例不是文章中“结构规则标记行与 clone 行取并集”的完整 Verbosity，也不应将不同扫描器口径下的 Erosion 直接比较。

架构约束单独放在测试中：`Heartbeat.Domain.Tests/ArchitectureTests` 检查编译后的 Domain 程序集不引用 EF Core。它证明这条依赖边界，不代表自动判断整个架构是否合理。

相对基点的 ratchet 拦增量，提交在仓库里的存量预算拦总量：只有 ratchet 时，已经存在的热点可以无限期驻留，换个基点甚至看不见它们。LOC 是烟雾报警器，不单独作为失败条件。分析器缺失或无法运行会失败并给出准备命令，避免“没有测到”被误报成通过。

### 存量记录（2026-09-17，`--base anchor --stock`）

第一次可度量的存量：生产 11,564 行（锚点 609 行），测试 8,781 行，工具 4,362 行，构建描述 443 行；生产重复 14 簇（重复行 1.05%），测试重复 15 簇（1.62%）；复杂度大于 10 的生产函数 12 个（当前预算 12，最高 17），Erosion 40.11%，CA1506 耦合热点 2 个。这是一次快照，不是承诺；要看当下的数字就重跑那条命令，别信这段文字。

## 可复现场景

```bash
./scripts/heartbeat-dev scenario replay-fixture
./scripts/heartbeat-dev scenario delivery
./scripts/heartbeat-dev scenario native-desktop
```

这三条命令不是同一种东西。`replay-fixture` 和 `delivery` 是复跑第一层测试的一个子集并把证据留下来，它们不比对应的测试多证明任何事；只有 `native-desktop` 会起进程、起数据库、要人真的去操作，是没有测试替身能代替的那一层。

| 场景 | 实际是什么 | 明确不证明 |
| --- | --- | --- |
| `replay-fixture` | 复跑浏览器测试 `replay.spec.ts`（登录后回放交互、响应式布局、截图），保留证据 | 认证和 API 均为 fixture，不是部署后的真实链路；它不是独立的第三层场景 |
| `delivery` | 复跑集成测试子集 `RecordUploadHttpTests`、`RecordReplayHttpTests`（API 与 PostgreSQL），保留证据 | 没有把 Web、Hub、Collector 作为一条真实链路运行；它不是独立的第三层场景 |
| `native-desktop` | 独立临时 Hub、真实 macOS Collector、人工输入时间窗、进程与队列元数据 | 不自动操作权限、锁屏、休眠或键鼠，也不默认保存用户内容 |

原生场景默认只保存时间、退出状态和 Hub 队列元数据。只有显式传入 `--include-sensitive-evidence` 才保留 Collector 日志；该日志可能含用户上下文。失败环境默认也会清理，只有诊断时显式使用 `--keep-environment-on-failure` 才保留独立 Compose 项目。

涉及 UI、用户流程、HTTP 展示或性能的改动需要场景证据；纯内部重构不强制截图。真实链路能力可以在后续加入新的 scenario，而不改变现有入口。

## 现场探针

规则里的时间参数（多久算稳定、多久算中断）不能靠猜。探针在宿主机上采一段真实读数，只回答「现在会切出多少条 Record，静置多久能压掉多少」，不改任何行为。

```bash
./scripts/heartbeat-dev probe window-title --duration-seconds 120
./scripts/heartbeat-dev probe window-title --duration-seconds 300 --poll-milliseconds 500
./scripts/heartbeat-dev probe window-title --readings <path> --dwell-seconds 1,1.5,2
./scripts/heartbeat-dev probe window-title --from-database --since '2026-09-16 13:00' --until '2026-09-18 13:00'
```

`--readings` 分析已经采好的读数文件，不再观察宿主机。定参数时值得多试几组 `--dwell-seconds`：默认那组只是量级探路，真要下结论得对着实测出来的抖动周期取值。

`window-title` 同时记录两路读数：原生通知送来的与轮询读到的。两路合起来才能分清「标题真的在变」和「通知没送到」。产物落在同一套证据目录：`window-title-readings.json` 是逐条读数，`window-title-churn.json` 是统计与候选静置参数的模拟结果。

探针不连 Hub、不写数据库、不起容器。数据库里存的是投影之后的 Record，已经被当前切分规则改写过，用它评估切分规则等于用结论证明前提。

`--from-database` 是明知这一点之后的退让：拿不到 Accessibility 权限、或者需要好几天而不是一次会话的样本时，它把本地数据库里的前台窗口 Record 导成同一种读数格式，走同一套分析和报告。它答不了「原生通知有没有漏送」，这条限制会写进运行清单。时间戳不带偏移时按本机时区理解，`--until` 缺省到现在；新旧两种数据形态都认（`desktop.window.foreground`，以及拆 Track 之前存在应用 Record 里的 `value.window.title`）。它需要本地栈的数据库在跑：`./scripts/heartbeat-dev env up db`。

默认只保存时间、应用身份、标题长度、标题指纹和相邻标题的形态（共同前缀、共同后缀、是否互为旋转）；滚动字幕与进度条这两种噪声靠形态就能认出来，不需要标题原文。`--include-sensitive-evidence` 才写入标题原文，数据库导出也一样。

探针读标题同样要 Accessibility 权限，而权限属于启动它的那个终端程序。整段读不到标题时，探针不会把它报成「标题很稳定」，而是明确说明读数无效并以非零退出码结束。

探针里的静置模拟不是规则本身。规则权威是 `src/Collectors/Heartbeat.Collector.Desktop.Mac/DesktopRecordProjector.cs`（`ObserveWindowTitle` / `SettlePendingTitle`）；`tools/Heartbeat.Dev/WindowTitleChurn.cs` 里的 `Simulate` 是它的一份模拟，只用来在真实读数上比不同静置参数。两侧靠一张共享场景表对账：`tests/window-title-dwell-scenarios.json` 每行给出同一串读数在同一静置参数下「模拟会留几条 Record」和「生产规则会留几条 Record」，`tests/Heartbeat.Dev.Tests/WindowTitleDwellReconciliationTests.cs` 钉住前者，`tests/Heartbeat.Collector.Desktop.Mac.Tests/WindowTitleDwellReconciliationTests.cs` 用真实 projector 钉住后者。改了任一侧而没改另一侧，对应的测试就红。

两个数字不一致的行必须在表里写清原因，测试会检查这一点。目前有意的差异有两处：读不到标题时生产规则只结束窗口区间、不开新 Record，而探针把它当成一个观测值（所以探针的条数在标题读不到时是上界，报告里同时给出 `titleUnavailableReadings`）；静置为 0 时最后一条读数带来的新标题，生产要等下一次读数才写出，模拟按探针结束时刻算，差最多一条。

## 证据与清理

```bash
./scripts/heartbeat-dev artifacts list
./scripts/heartbeat-dev artifacts prune
./scripts/heartbeat-dev artifacts prune --apply
./scripts/heartbeat-dev artifacts prune --keep 20 --keep-failed 5 --older-than-days 14 --apply
./scripts/heartbeat-dev artifacts inventory-local
```

每次 verify/quality/scenario/probe 创建运行目录后都会写入 `.artifacts/verification/<run-id>/manifest.json`，记录执行时间、退出码、命令、产物、敏感证据标志和验证边界。进程启动异常和可捕获的取消也会留下失败清单（取消退出码 130）；强制杀进程、断电或产物目录无法写入不在此保证内。原生场景的环境清理失败会明确记录未确认清理，不会声称环境已经删除。

保留规则的默认值是「最近 10 次、最近 5 次失败、2 天内的都留」，并且每次 verify/quality/scenario/probe 跑完会自动按这套默认值清一次，输出清掉了几个运行、释放了多少。之前的默认值是保留 20 次且 14 天以上才删，在一天跑十几次的节奏下等于空操作，证据目录因此长到数 GiB（本机第一次自动清理就回收了 5.01 GiB / 38 个运行）；失败的运行单独保一批，因为出问题那次的产物才最值得留。没有 manifest 的运行按失败对待——它是中途死掉的那种。留在窗口内的当天运行不会被清掉，所以跑得多的那天证据目录仍然是 GiB 级别：默认值的作用是不让上周的运行一直躺着，不是压到最小。

Web 检查和 Replay 场景在每次运行的 `web-workspace/` 源码副本中执行，使用独立的 Next 输出、TypeScript 生成文件和浏览器端口。副本共享已安装的 `node_modules`（Windows 使用 junction），不复制 `.env*`、旧构建产物或符号链接源文件；运行期间不要修改共享依赖。`HEARTBEAT_VERIFICATION_ROOT` 仅用于让 Turbopack 解析副本与共享依赖。这样同时运行验证不会竞争主工作树的 `.next` 或修改其 TypeScript 配置。

手动 `prune` 默认为 dry-run；只有 `--apply` 删除已列出的验证产物，包括运行副本，不删除链接指向的共享依赖。基线工作树缓存（`.artifacts/quality-baselines/`）不在 `prune` 范围内：它是可重建的缓存，删掉只会让下一次跨基点扫描变慢。

`inventory-local` 只生成旧 `.local` 的大小、文件数、更新时间和脚本清单，供人工确认后另行清理；它永远不删除 `.local`。

## Developer CLI 的边界

这套 CLI 只做工程验证，不是产品的一部分，也不替人做判断。说清它不做什么，比列它做什么更有用：

- 它不判断架构是否合理。质量闸门量的是重复、复杂度和 LOC 这些可数的东西；依赖边界靠 `ArchitectureTests` 这类测试，设计取舍靠人和 ADR。
- 它不证明部署后的真实链路可用。除 `native-desktop` 外的场景都是复跑测试子集。
- 它的质量结论永远相对某个基点，再加一个提交在仓库里的存量上限。基点选错时它现在会说“基点不可用”，但它无法替你判断哪个基点在语义上是对的。
- 它不改生产代码，也不替生产代码做决定。探针的模拟规则以 `src/` 下的实现为准，靠对账测试保持同步。
- 它不在 CI 里跑：仓库里没有流水线，命令要人或 Agent 主动执行；`.artifacts/` 是本机证据，不是共享的历史记录。
- 它不评价工作质量。所有产物都写在 `.artifacts/verification/<run-id>/`，退出码和 manifest 里的 limitations 是它给出的全部结论。

## 维护规则

当路由、用户入口、稳定 selector、renderer 或场景变化时，同步更新项目验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。Feature Map 只保存到达方式和验证路径，领域与协议事实继续链接 `CONTEXT.md`、ADR 或功能文档，避免出现第二份真相来源。
