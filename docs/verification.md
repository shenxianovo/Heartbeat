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

普通脏工作树以 `HEAD` 为比较基点：

```bash
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --plan
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD
dotnet run --project tools/Heartbeat.Dev -- verify full
```

`changed` 按路径选择 .NET、Developer CLI、前端静态检查和 Playwright；无法识别的路径扩为 `full`。干净工作树必须显式提供比较基点。契约文档会选择相应测试，其他纯文档改动可能没有可执行检查；`--plan` 的输出是本次选择的权威说明。

各检查日志和汇总写入同一次验证目录。普通失败后继续其他检查；取消立即停止并返回 `130`；进程无法启动或证据无法写入时立即失败。

## 结构质量

```bash
dotnet run --project tools/Heartbeat.Dev -- quality loc
dotnet run --project tools/Heartbeat.Dev -- quality --base HEAD
dotnet run --project tools/Heartbeat.Dev -- quality --base anchor --stock
```

`quality loc` 只统计工作树有效行，不生成验证证据。`quality --base` 检查重复、函数复杂度、分析完整性和绝对预算，并把报告写入 `.artifacts/verification/<run-id>/quality.json`。

闸门规则：

- 新增重复簇失败；
- 函数新跨过复杂度 10，或超过 10 后继续增长，失败；
- 复杂度热点超过 `tools/Heartbeat.Dev/quality-budget.json` 的预算，失败；
- 工具缺失、扫描不完整、基点无生产函数或函数定位失败，失败；
- LOC、Erosion 和类型耦合只观察，不单独阻断。

存量预算只能下调；确需上调时修改预算文件并说明理由。C# 分析会编译普通项目以及 AppKit / WinUI 宿主的托管部分，但不证明原生应用能够打包或运行。

## 可复现场景

```bash
dotnet run --project tools/Heartbeat.Dev -- scenario --list
dotnet run --project tools/Heartbeat.Dev -- scenario replay-fixture
dotnet run --project tools/Heartbeat.Dev -- scenario hubs-fixture
dotnet run --project tools/Heartbeat.Dev -- scenario delivery
dotnet run --project tools/Heartbeat.Dev -- scenario collector-delivery
dotnet run --project tools/Heartbeat.Dev -- scenario desktop-replay
dotnet run --project tools/Heartbeat.Dev -- scenario native-desktop
```

| 场景 | 证明 | 不证明 |
| --- | --- | --- |
| `hubs-fixture` | Hub 状态分离、在线启停、离线禁用和配置表单 | 真实认证、API 或原生启停 |
| `replay-fixture` | 回放交互、响应式布局和 Chromium 截图 | 真实认证与端到端链路 |
| `delivery` | API 与 PostgreSQL 的上传、重放集成 | Web、Hub 或 Collector |
| `collector-delivery` | 真实 macOS 单次采集、Hub 接管、后端注册、落库和队列清空 | 持续采样、物理输入、权限、锁屏、休眠或 Web |
| `desktop-replay` | 本地开发应用、真实 Auth、原生采集、进程内 Hub、隔离后端与同一 Record 的 Web 回放 | 普通构建 Keychain、Windows、发行或系统权限交互 |
| `native-desktop` | 临时 Hub、真实 macOS Collector、进程和队列元数据 | 权限、锁屏、休眠或物理输入 |

UI、用户流程或 HTTP 展示变更运行相应场景。活动泳道性能变更运行[前端生产基准](../src/Frontend/Heartbeat.Web/README.md#活动泳道拖动基准)；若交互行为也变化，同时运行 `replay-fixture`。原生场景默认不保存用户内容，只有显式使用 `--include-sensitive-evidence` 才保存 Collector 日志。

### 组合实现来验证行为

场景按验收目的直接组合生产实现与小型运行设施；顺序执行多个独立测试不等于验证组件之间的真实交互。新增行为优先扩展现有场景，只有出现独立验收目的时才增加入口。责任决策见 [ADR-0013](adr/ADR-0013-scenarios-compose-implementations.md)。

### Collector 到落库

采集启动、Hub 接管、注册或上传链路变化时运行 `scenario collector-delivery`。它需要已登录的 macOS 桌面、Docker，以及 `.env.local` 中有效的 Owner 与 API key。

场景使用独立 Compose 项目、临时 PostgreSQL/SQLite 和独立 Target。真实 Collector 在 API 尚未启动时执行一次采集，Hub 持久接管后再启动 API；随后核对 Owner、Target、采集时间窗、前台应用 Record、落库数量和空队列。场景不手工提交 Record，也不预注册资源。

### 桌面应用到真实 Web 回放

`scenario desktop-replay` 打包并启动 Heartbeat Dev，使用临时 profile、真实 Auth、Mac 原生采集、进程内 Hub、隔离后端和生产 Web。按终端提示完成客户端连接、开始/暂停/退出及 OIDC 登录。

场景核对客户端自身的前台 Record、退出后的 SQLite 队列、后端数据和浏览器详情中的同一 Record ID。临时 profile、开发凭据、浏览器会话和隔离环境在结束时清理；显式使用 `--keep-environment-on-failure` 只保留失败的 Docker 环境。该场景不替代普通构建 Keychain、Windows 或发行验收。

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
