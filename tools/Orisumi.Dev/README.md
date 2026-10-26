# Orisumi.Dev

Orisumi.Dev 是人和 Agent 共用的开发工具，负责启动本地环境、构建、测试、质量扫描、冒烟场景和验证证据。为什么由它组织验证，见 [ADR · 开发验证与测试证据](../../src/Docs/Orisumi.Docs/content/docs/adr/verification.mdx)；为什么由它启动环境，见 [ADR · 开发环境的启动与隔离](../../src/Docs/Orisumi.Docs/content/docs/adr/development-environment.mdx)。

环境中的资源、依赖关系和端口由 AppHost 定义，见 [AppHost README](../Orisumi.AppHost/README.md)。

## 准备

需要 `global.json` 指定的 .NET SDK。启动环境需要 Docker 和 Aspire CLI，安装方式见[仓库 README · 启动本地环境](../../README.md#启动本地环境)。文档站、前端检查和质量扫描还需要 Node.js、npm 和 pnpm，集成测试需要 Docker。

首次使用时，在仓库根目录安装文档站、前端和扫描工具的依赖：

```sh
pnpm --dir src/Docs/Orisumi.Docs install --frozen-lockfile
npm --prefix src/Clients/Orisumi.Web ci
npm --prefix tools/Orisumi.Dev/jscpd ci --ignore-scripts
```

## 安装

Orisumi.Dev 是 .NET 全局工具，命令名是 `orisumi`。在仓库根目录运行：

```sh
dotnet run --project tools/Orisumi.Dev -- install
```

这条命令打包当前检出目录中的 Orisumi.Dev，然后安装或更新 `orisumi`。每次打包都生成新的版本号，所以更新后运行的总是刚打包的版本。Orisumi.Dev 改动后，再运行一次即可。

`orisumi` 在仓库中的任何目录都可以运行，它通过 Git 找到当前检出目录的根目录。`orisumi <命令>` 与 `dotnet run --project tools/Orisumi.Dev -- <命令>` 等价；某个分支修改了 Orisumi.Dev 时，用后一种形式运行这个分支的版本。

## 环境

```sh
orisumi env up                  # 启动全部服务
orisumi env up api              # 只启动 API 和它的依赖
orisumi env up api --temp       # 使用临时卷
orisumi env up --reset          # 重建默认卷
orisumi env status --json
orisumi env logs api --follow
orisumi env otel traces api
orisumi env restart api
orisumi env down
```

| 命令 | 作用 |
| --- | --- |
| `env up [服务...] [--temp \| --reset]` | 在后台启动环境，等所选服务就绪后返回，并输出状态 |
| `env down` | 停止环境。使用临时卷时，同时删除临时卷 |
| `env status [--json]` | 显示已启动的服务及其状态、入口地址、看板地址、数据库连接和数据卷 |
| `env logs <服务> [--follow]` | 读取服务的控制台日志 |
| `env otel <logs\|spans\|traces> [服务]` | 读取环境中的遥测 |
| `env restart <服务>` | 重启一个服务 |
| `env volumes` | 列出 Orisumi 的数据卷，以及每个卷所属的检出目录 |
| `env volumes prune [--apply]` | 预览或删除所属检出目录已不存在的卷 |

命令作用于当前检出目录的环境。`env logs`、`env otel` 和 `env restart` 的服务名可以是任何资源，包括 `db`、`migrate` 和 `nginx`。

### 服务

`env up` 可以选择以下服务，依赖由 AppHost 补齐。不选择时启动全部服务。

| 服务 | 一同启动 |
| --- | --- |
| `api` | `db`、`migrate` |
| `docs` | — |
| `web` | `api`、`db`、`migrate` |

NGINX 总是启动，提供固定的入口端口。各入口在不同服务组合下的转发见 [AppHost README · 入口](../Orisumi.AppHost/README.md#入口)。

环境已经运行时，`env up` 报错并显示当前状态。要换一组服务或换一个卷，先运行 `env down`。

### 隔离与数据卷

每个检出目录同时运行一套环境。需要同时运行不同的代码时，创建 Git 工作树，在工作树中运行 `orisumi env up`。

| 检出目录 | 端口 | 默认卷 |
| --- | --- | --- |
| 主检出目录 | 固定端口，见 [AppHost README · 入口](../Orisumi.AppHost/README.md#入口) | `orisumi-aspire-postgres-data` |
| 工作树 | 首次启动时分配，保存在工作树的 `.artifacts/env/environment.json`，之后的启动沿用 | `orisumi-<目录名>-<路径哈希>` |

- 默认卷在 `env down` 后保留。
- `--temp` 使用名为 `orisumi-temp-<时间>` 的临时卷，`env down` 时删除。
- `--reset` 删除当前检出目录的默认卷，重新创建后启动，卷中的数据全部丢失。表结构与当前迁移不一致时使用，例如 Initial migration 改动之后。`--reset` 与 `--temp` 不能同时使用。
- 每个卷都带有所属检出目录的标签，`env volumes` 据此列出归属。工作树删除后，用 `env volumes prune --apply` 回收它的卷。

### 状态

`env status --json` 的输出：

```json
{
  "checkout": "/Users/me/Code/Orisumi",
  "running": true,
  "services": [
    { "name": "db", "state": "Running", "health": "Healthy" },
    { "name": "migrate", "state": "Finished", "exitCode": 0 },
    { "name": "api", "state": "Running", "health": "Healthy" },
    { "name": "nginx", "state": "Running", "health": "Healthy" }
  ],
  "entries": {
    "api": "http://localhost:8080/api",
    "docs": "http://localhost:3000/",
    "web": "http://localhost:8081/"
  },
  "dashboard": "https://localhost:18888",
  "database": {
    "connectionString": "Host=localhost;Port=54329;Database=orisumi;Username=orisumi;Password=orisumi",
    "volume": "orisumi-aspire-postgres-data",
    "temporary": false
  }
}
```

`entries` 列出全部入口，未启动服务的入口按 [AppHost README · 入口](../Orisumi.AppHost/README.md#入口) 的规则响应。环境没有运行时，`running` 是 `false`，其他字段省略。

## macOS 客户端

`orisumi mac` 构建 [macOS 客户端](../../src/Clients/Orisumi.Mac/README.md)，并让它连接本检出目录的环境。只在 macOS 上可用。

```sh
orisumi mac build
orisumi mac run --build --window
orisumi mac stop
```

| 命令 | 作用 |
| --- | --- |
| `mac build [--debug]` | 用 `xcodebuild` 构建客户端到 `.artifacts/mac`，默认 Release |
| `mac run [--build] [--window]` | 启动已构建的 Release 客户端，连接本检出目录环境的 API 和 Web 控制台。`--build` 先构建，`--window` 启动时打开窗口 |
| `mac stop` | 退出从本检出目录启动的客户端。客户端先让 helper 结束当前时段，再退出 |

- `mac run` 要求环境运行中、API 已启动；客户端已在运行时报错，先运行 `mac stop`。
- 每个数据卷对应一个程序实例：客户端的身份文件、登记文件和 SDK 状态放在 `.artifacts/mac/instances/<卷名>/`。所以本地待发送的版本只会发往它所属的后端。删除卷之后，可以一并删除对应的目录。
- 日常使用的客户端从 `/Applications` 启动，使用默认的程序实例，与这里的实例互不影响。

## 验证

```sh
orisumi verify full
orisumi verify changed --base HEAD
orisumi verify changed --base HEAD --quality
orisumi verify scenarios
orisumi verify scenarios api
```

| 命令 | 作用 |
| --- | --- |
| `verify full` | 构建 .NET 解决方案，运行模型测试、集成测试和开发工具测试，运行前端的 `npm run verify`，检查并构建文档站；在 macOS 上还运行客户端的 `swift test` 并构建客户端 |
| `verify changed` | 根据相对基点的改动选择检查。无法识别的路径触发全量检查 |
| `verify changed --quality` | 在同一次运行中执行变更检查，再以同一基点运行质量扫描 |
| `verify scenarios [场景...]` | 运行冒烟场景。不指定时运行全部场景 |

- 加 `--plan` 只显示会运行哪些检查或场景，不执行。加 `--json` 以 JSON 输出计划。
- 有未提交的改动时，`verify changed` 可以省略 `--base`，默认使用 `HEAD`。没有改动时，必须指定 `--base`。
- 构建失败、测试失败、场景失败或扫描未完成时，命令返回失败。质量扫描的发现不使命令失败，供评审参考。
- 构建和自动测试不覆盖容器运行、页面和交互，运行时行为由冒烟场景检查。热更新和页面另外检查。

### 冒烟场景

场景对本检出目录的环境执行，检查运行中的服务。每个场景写明前提；前提不满足时，场景报告为跳过并写明原因，跳过不使命令失败。`--plan` 显示每个场景的前提当前是否满足。

| 场景 | 前提 | 检查 |
| --- | --- | --- |
| `environment` | 本检出目录没有运行中的环境 | 用临时卷启动环境：各入口的转发、超过 10 MiB 的请求返回 `413`、只启动部分服务时 `/api` 返回 `503`、迁移失败时 API 不启动、停止后再启动数据仍在。结束时停止环境并删除卷 |
| `api` | 环境运行中，API 已启动，使用临时卷 | 通过 8080 入口提交版本、读回版本，以及提交被拒绝时的响应 |

写入数据的场景要求临时卷，场景数据不进入默认卷。每个 Observer 有一个场景：对环境运行几轮采样，再通过 API 读回结果。依赖本机能力的场景写明要求，例如 macOS。

## 质量与行数

```sh
orisumi quality --base HEAD
orisumi loc
```

两个命令都可以加 `--json`。

`quality` 扫描业务代码、文档站和开发工具，并与基点比较：

| 检查 | 工具 | 报告方式 |
| --- | --- | --- |
| 复杂度和耦合 | C# 编译器分析器、ESLint | 按模块报告，突出复杂度大于 10 的函数及其变化 |
| 重复代码 | jscpd，最少 8 行、70 个 token | 分别报告生产代码、测试和工具 |
| 未使用代码 | C# 编译器分析器、Knip | 未使用的私有成员、文件、导出和依赖 |

- 扫描结果需要结合行为判断。业务需要的复杂代码可以保留。扫描不证明完整的调用关系，也不检查所有 NuGet 依赖是否有用。
- 基点必须能完成分析构建。历史代码无法编译时，命令报告比较未完成并返回失败，但保留当前代码的扫描结果。
- JS 扫描工具的版本固定在 `jscpd/package-lock.json`。

`loc` 统计已跟踪和未忽略的文件，不构建也不扫描。统计排除空行和纯注释行，按模块、语言和类别分类：

| 类别 | 包括 |
| --- | --- |
| 生产 | 业务代码 |
| 测试 | 测试代码，包括共用的测试工具 |
| 工具 | 开发工具 |
| 构建 | 构建和项目配置 |
| 文档 | MDX 和 OpenAPI 契约 |
| 生成 | 锁文件等生成文件 |

复杂字符串和嵌入的其他语言可能使行数有误差。

## 证据

每次验证的证据保存在 `.artifacts/verification/<run-id>/`：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 执行清单 |
| `verification.json` | 验证结果，包括每个场景的通过、失败或跳过及原因 |
| `quality.json` | 质量报告，加 `--quality` 或运行 `quality` 时生成 |
| 步骤日志 | 每个步骤和场景的输出，场景日志包括它发出的请求和收到的响应 |

成功、失败和取消的运行都保存。TUnit 的原始报告在 `TestResults/`，由文档站读取。

```sh
orisumi artifacts list
orisumi artifacts prune           # 只预览
orisumi artifacts prune --apply   # 删除
```

每次验证结束时自动清理。满足任一条件的运行会保留：

- 最近 10 次运行。
- 最近 5 次没有成功的运行，包括失败、取消和缺少清单的运行。
- 2 天内的全部运行。

`artifacts prune` 使用同样的条件。

质量扫描的基线工作树缓存在 `.artifacts/quality-baselines/<commit>/`，不参与上述清理。需要回收空间时，移除对应的 Git worktree，下次扫描会重新创建。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 130 | 已取消 |
| 其他非零值 | 执行失败 |

## 代码组织

| 目录 | 内容 |
| --- | --- |
| `Environment` | 启动、停止和读取环境，分配端口，管理数据卷 |
| `Clients` | 构建、启动和停止 macOS 客户端 |
| `Verification` | 选择和执行检查 |
| `Scenarios` | 冒烟场景 |
| `Quality` | 分类和扫描源码 |
| `Artifacts` | 保存和管理证据 |
| `Infrastructure` | 运行进程，定位仓库 |

命令用 System.CommandLine 注册，解析后把类型化的选项直接传给实现，不递归调用 CLI。
