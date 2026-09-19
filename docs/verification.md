# 工程验证

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
./scripts/heartbeat-dev quality --base HEAD
./scripts/heartbeat-dev quality --base anchor --stock
```

`--base HEAD` 阻止本次改动增加坏味道。`--base anchor --stock` 使用重写锚点 `4e15d57` 查看当前存量；存量模式只报告相对锚点的增量，但仍检查分析完整性和绝对预算。基点中没有 `src/` 生产代码时直接失败。

报告写入 `.artifacts/verification/<run-id>/quality.json`，包含：

- 按生产、测试、工具、构建、生成代码和未归类文件统计的有效 LOC；
- jscpd 检出的生产与测试重复，阈值为 8 行、70 token；
- C# CA1502 与 TypeScript ESLint 检出的函数圈复杂度；
- 复杂度热点占维护负担的 Erosion；
- C# CA1506 类型耦合热点。

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
./scripts/heartbeat-dev scenario delivery
./scripts/heartbeat-dev scenario native-desktop
./scripts/heartbeat-dev scenario --list
```

| 场景 | 证据 | 不证明 |
| --- | --- | --- |
| `replay-fixture` | 登录后回放交互、响应式布局和 Chromium 截图 | 认证和 API 是 fixture，不是实际端到端链路 |
| `delivery` | API 与 PostgreSQL 的上传、重放集成测试 | 不覆盖 Web、Hub 或 Collector |
| `native-desktop` | 临时 Hub、真实 macOS Collector、进程和队列元数据 | 不自动操作权限、锁屏、休眠或物理输入 |

UI、用户流程或 HTTP 展示变更需要相应场景证据。活动泳道性能变更运行[前端基准](../src/Frontend/Heartbeat.Web/README.md#活动泳道拖动基准)；若交互行为也变了，同时运行 `replay-fixture`。基准由前端脚本保存报告，退出码只说明测量是否完成，不证明性能达标。原生场景默认不保存用户内容；只有显式使用 `--include-sensitive-evidence` 才保存 Collector 日志。`--keep-environment-on-failure` 会在失败时保留独立环境。

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
- 除 `native-desktop` 外，现有 scenario 都是带证据的测试子集。
- 质量结论只对指定 Git 基点和仓库存量预算成立。
- fixture、mock 或局部集成结果不能描述成真实端到端验证。
- 最终报告必须列出检查项、结果、证据路径和未覆盖范围。

路由、用户入口、稳定 selector、renderer 或场景命令变化时，同步更新验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。
