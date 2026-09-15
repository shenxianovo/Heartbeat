# 工程验证

Heartbeat 把“测试通过”“结构没有继续腐蚀”“用户场景有可检查证据”分成三层验证。统一入口是 .NET 10 Developer CLI：macOS/Linux 运行 `./scripts/heartbeat-dev`，Windows 运行 `scripts/heartbeat-dev.cmd`。自己启动开发环境和 Agent 自验证使用同一套实现。

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

## 结构质量

```bash
./scripts/heartbeat-dev quality --base HEAD
```

质量报告写入 `.artifacts/verification/<run-id>/quality.json`，包含：

- 生产代码有效 LOC 的基线、当前值和语言增量；测试、工具代码单独统计。锁文件、生成代码、EF Designer 和 Model Snapshot 不计入生产语料，Initial migration 保留。
- jscpd 精确 clone 增量。只阻止生产代码中新出现的至少 8 行、70 token 重复；另生成不参与闸门的观察报告，用标识符归一化、相邻缺口和 AST 相似度寻找测试重复、改名重复与近似重复。
- 圈复杂度增量与 Erosion。C# 使用 CA1502，TypeScript 使用 ESLint `complexity`；报告复杂度大于 10 的生产函数，只阻止新跨过 10 或已经高于 10 后继续增长的函数。Erosion 是“复杂度 × 函数有效行数”的维护负担中，由复杂度大于 10 的函数贡献的比例；报告基线、当前值与增量，暂作观察指标，不单独阻止提交。
- C# 类型耦合观察。SDK 内置 Roslyn Analyzer 的 CA1506 报告引用类型过多的类型／成员，沿用其默认阈值（类型 95、其他符号 40）。`complexity.coupling` 包含基线与当前生产代码热点；只作观察，不影响质量闸门。普通构建继续使用 `Directory.Build.props` 的推荐规则，扫描专用规则只在 `quality` 构建中开启。

C# 函数范围由 Roslyn 语法树识别，有效行按语法 token 覆盖的行计数，不再通过分号和大括号猜测边界。主构造函数计声明及字段／属性初始化器，顶层入口计顶层语句；内嵌函数的源码仍属于外层函数范围。C# 函数身份包含命名空间、类型、成员和参数类型，TypeScript 使用语法作用域和声明名称；诊断按行列位置匹配，区分同一行的多个函数。没有名称的回调通过所属作用域内的顺序区分：插入或重排这类回调可能改变匹配，重命名或移动文件也视为新的函数身份。

C# 构建诊断固定为英文并保留在 `baseline-csharp.log`、`current-csharp.log`。TypeScript 也保留 ESLint 的隐式函数口径，单独测量类字段初始化器和静态块。分析失败、函数定位失败或 TypeScript 函数未完整测量都会使检查失败；不会用复杂度 1 静默填补漏测。

这些指标用于观察本项目趋势。重复行比例不是文章中“结构规则标记行与 clone 行取并集”的完整 Verbosity，也不应将不同扫描器口径下的 Erosion 直接比较。

架构约束单独放在测试中：`Heartbeat.Domain.Tests/ArchitectureTests` 检查编译后的 Domain 程序集不引用 EF Core。它证明这条依赖边界，不代表自动判断整个架构是否合理。

这是相对 Git 基点的 ratchet，不提交一份会逐渐失真的债务基线。LOC 是烟雾报警器，不单独作为失败条件。分析器缺失或无法运行会失败并给出准备命令，避免“没有测到”被误报成通过。

## 可复现场景

```bash
./scripts/heartbeat-dev scenario replay-fixture
./scripts/heartbeat-dev scenario delivery
./scripts/heartbeat-dev scenario native-desktop
```

| 场景 | 覆盖 | 明确不证明 |
| --- | --- | --- |
| `replay-fixture` | Chromium 中的登录后回放交互、响应式布局、截图 | 认证和 API 均为 fixture，不是部署后的真实链路 |
| `delivery` | API 与 PostgreSQL 的上传、重放集成测试 | 没有把 Web、Hub、Collector 作为一条真实链路运行 |
| `native-desktop` | 独立临时 Hub、真实 macOS Collector、人工输入时间窗、进程与队列元数据 | 不自动操作权限、锁屏、休眠或键鼠，也不默认保存用户内容 |

原生场景默认只保存时间、退出状态和 Hub 队列元数据。只有显式传入 `--include-sensitive-evidence` 才保留 Collector 日志；该日志可能含用户上下文。失败环境默认也会清理，只有诊断时显式使用 `--keep-environment-on-failure` 才保留独立 Compose 项目。

涉及 UI、用户流程、HTTP 展示或性能的改动需要场景证据；纯内部重构不强制截图。真实链路能力可以在后续加入新的 scenario，而不改变现有入口。

## 证据与清理

```bash
./scripts/heartbeat-dev artifacts list
./scripts/heartbeat-dev artifacts prune --keep 20 --older-than-days 14
./scripts/heartbeat-dev artifacts prune --keep 20 --older-than-days 14 --apply
./scripts/heartbeat-dev artifacts inventory-local
```

每次 verify/quality/scenario 创建运行目录后都会写入 `.artifacts/verification/<run-id>/manifest.json`，记录执行时间、退出码、命令、产物、敏感证据标志和验证边界。进程启动异常和可捕获的取消也会留下失败清单（取消退出码 130）；强制杀进程、断电或产物目录无法写入不在此保证内。原生场景的环境清理失败会明确记录未确认清理，不会声称环境已经删除。

Web 检查和 Replay 场景在每次运行的 `web-workspace/` 源码副本中执行，使用独立的 Next 输出、TypeScript 生成文件和浏览器端口。副本共享已安装的 `node_modules`（Windows 使用 junction），不复制 `.env*`、旧构建产物或符号链接源文件；运行期间不要修改共享依赖。`HEARTBEAT_VERIFICATION_ROOT` 仅用于让 Turbopack 解析副本与共享依赖。这样同时运行验证不会竞争主工作树的 `.next` 或修改其 TypeScript 配置。

`prune` 默认为 dry-run；只有 `--apply` 删除已列出的验证产物，包括运行副本，不删除链接指向的共享依赖。

`inventory-local` 只生成旧 `.local` 的大小、文件数、更新时间和脚本清单，供人工确认后另行清理；它永远不删除 `.local`。

## 维护规则

当路由、用户入口、稳定 selector、renderer 或场景变化时，同步更新项目验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。Feature Map 只保存到达方式和验证路径，领域与协议事实继续链接 `CONTEXT.md`、ADR 或功能文档，避免出现第二份真相来源。
