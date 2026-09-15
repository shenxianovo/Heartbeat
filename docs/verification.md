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

`changed` 根据集中维护的路径映射选择 .NET、Developer CLI、前端静态检查和 Playwright。无法识别的路径自动扩为 `full`，而不是猜测可以少跑。干净工作树必须显式给出 `--base`；脏工作树省略时才默认 `HEAD`。

## 结构质量

```bash
./scripts/heartbeat-dev quality --base HEAD
```

质量报告写入 `.artifacts/verification/<run-id>/quality.json`，包含：

- 生产代码有效 LOC 的基线、当前值和语言增量；测试、工具代码单独统计。锁文件、生成代码、EF Designer 和 Model Snapshot 不计入生产语料，Initial migration 保留。
- jscpd 精确 clone 增量。只阻止生产代码中新出现的至少 8 行、70 token 重复；另生成不参与闸门的观察报告，用标识符归一化、相邻缺口和 AST 相似度寻找测试重复、改名重复与近似重复。
- 圈复杂度增量与 Erosion。C# 使用 CA1502，TypeScript 使用 ESLint `complexity`；报告复杂度大于 10 的生产函数，只阻止新跨过 10 或已经高于 10 后继续增长的函数。Erosion 是“复杂度 × 函数有效行数”的维护负担中，由复杂度大于 10 的函数贡献的比例；报告基线、当前值与增量，暂作观察指标，不单独阻止提交。

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

每次 verify/quality/scenario 都写入 `.artifacts/verification/<run-id>/manifest.json`，记录执行时间、退出码、命令、产物、敏感证据标志和验证边界。Replay 与 verify 中的 Playwright 使用每次运行独立的本地端口，允许多个验证并行执行。`prune` 默认为 dry-run；只有 `--apply` 删除已列出的验证产物。

`inventory-local` 只生成旧 `.local` 的大小、文件数、更新时间和脚本清单，供人工确认后另行清理；它永远不删除 `.local`。

## 维护规则

当路由、用户入口、稳定 selector、renderer 或场景变化时，同步更新项目验证 skill 的 [Feature Map](../.agents/skills/verify-heartbeat/references/features/README.md)。Feature Map 只保存到达方式和验证路径，领域与协议事实继续链接 `CONTEXT.md`、ADR 或功能文档，避免出现第二份真相来源。
