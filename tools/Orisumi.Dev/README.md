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

Orisumi.Dev 是 .NET 全局工具，命令名是 `orisumi`。首次安装时，在仓库根目录运行：

```sh
dotnet run --project tools/Orisumi.Dev -- tool install
```

这条命令打包当前检出目录中的 Orisumi.Dev，然后安装或更新 `orisumi`。每次打包都生成新的版本号，所以更新后运行的总是刚打包的版本。Orisumi.Dev 改动后，运行 `orisumi tool install` 更新。

`orisumi` 在仓库中的任何目录都可以运行，它通过 Git 找到当前检出目录的根目录。`orisumi <命令>` 与 `dotnet run --project tools/Orisumi.Dev -- <命令>` 等价；某个分支修改了 Orisumi.Dev 时，用后一种形式运行这个分支的版本。

## 命令

| 命令 | 作用 |
| --- | --- |
| `env` | 本检出目录的环境，以及本机的数据卷 |
| `mac` | 构建和运行 macOS 客户端 |
| `observers` | 构建 Runtime 和 Observer 的安装包 |
| `verify` | 构建与自动化检查、冒烟场景 |
| `analyze` | 质量扫描和行数统计 |
| `runs` | 验证与质量扫描的运行记录 |
| `tool` | 安装和更新 `orisumi` 自身 |

只输入命令组时显示该组的帮助。

### 输出

命令默认输出给人读的文字，带 `--json` 选项的命令加上它输出 JSON。文字的第一行是“主题 · 范围 · 结论”，例如 `Quality · against HEAD (cd2bedb) · nothing new`。状态记号如下：

| 记号 | 含义 |
| --- | --- |
| ✓ | 成功 |
| ✗ | 失败 |
| ● | 运行中 |
| ○ | 等待或已停止 |
| ⊘ | 跳过或取消 |

仓库内的路径写成相对仓库根目录的形式，主目录写成 `~`。

在支持 ANSI 的交互终端里，输出带颜色，长时间的步骤原地刷新转圈和已用时间，每秒一次。输出被重定向、`TERM=dumb`、设置了 `CI` 时，输出纯文本，不刷新；设置了 `NO_COLOR` 时不着色。Agent 在伪终端里运行命令时，设置 `TERM=dumb` 得到纯文本。

## 环境

| 命令 | 作用 |
| --- | --- |
| `env up [服务...] [--temp \| --reset]` | 在后台启动环境，等所选服务就绪后返回，并输出状态 |
| `env down` | 停止环境。使用临时卷时，同时删除临时卷 |
| `env status [--json]` | 显示已启动的服务及其状态、入口地址、看板地址、数据库连接和数据卷 |
| `env logs <资源> [--follow]` | 读取资源的控制台日志 |
| `env restart <资源>` | 重启一个资源 |
| `env telemetry <logs\|spans\|traces> [资源]` | 读取环境中的遥测 |
| `env volumes list` | 列出本机全部 Orisumi 数据卷，以及每个卷所属的检出目录 |
| `env volumes prune [--apply]` | 预览本机所属检出目录已不存在的卷，加 `--apply` 删除 |

`env volumes` 作用于本机全部带 Orisumi 标签的数据卷，其他命令作用于当前检出目录的环境。资源可以是 `env up` 的任一服务，也可以是 `db`、`migrate` 和 `nginx`。

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
- 每个卷都带有所属检出目录的标签，`env volumes list` 据此列出归属。工作树删除后，用 `env volumes prune --apply` 回收它的卷。

### 状态

`env status --json` 输出检出目录、各服务的状态、入口地址 `entries`、看板地址、数据库连接和数据卷。`entries` 列出全部入口，未启动服务的入口按 [AppHost README · 入口](../Orisumi.AppHost/README.md#入口) 的规则响应。环境没有运行时，`running` 是 `false`，其他字段省略。

## macOS 客户端

`orisumi mac` 构建 [macOS 客户端](../../src/Clients/Orisumi.Mac/README.md)，并让它连接本检出目录的环境。只在 macOS 上可用。

| 命令 | 作用 |
| --- | --- |
| `mac build [--configuration debug\|release]` | 用 `xcodebuild` 构建客户端到 `.artifacts/mac` |
| `mac run [--configuration debug\|release] [--build] [--window]` | 启动已构建的客户端，连接本检出目录环境的 API 和 Web 控制台。`--build` 先构建同一配置，`--window` 启动时打开窗口 |
| `mac stop` | 退出从本检出目录启动的客户端。客户端先让 Runtime 停止所有 Observer，再退出 |

`--configuration` 默认是 `release`。`mac run` 运行所选配置的客户端。

- `mac run` 要求环境运行中、API 已启动；客户端已在运行时报错，先运行 `mac stop`。
- 每个数据卷对应一个 Runtime 数据目录 `.artifacts/mac/instances/<卷名>/`，其中有安装的 Observer、各程序实例的身份和本地数据。实例身份和采集进度与后端的数据卷对应。删除卷之后，一并删除对应的目录，重新接入并从来源采集。
- 日常使用的客户端从 `/Applications` 启动，使用默认的数据目录，与这里的互不影响。

## Observer 安装包

`orisumi observers pack` 为一个平台发布 Runtime，并构建给出的 Observer 的安装包。macOS 客户端的构建和服务器的镜像都用它的输出。

| 命令 | 作用 |
| --- | --- |
| `observers pack <id...> --target <RID> --output <目录> [--sign <身份>]` | 把 Runtime 发布为单文件，放进 `<目录>/runtime/`；为每个安装包写出清单并打成 ZIP，放进 `<目录>/bundled/`。`--sign` 在 macOS 上用 `codesign` 签名 Runtime 和包中的可执行文件，`-` 是临时签名 |

- 安装包按 `src/Observers/` 下项目的 `ObserverPackageId` 找到，清单的名称和部署范围取自 `ObserverName` 和 `ObserverScope`，格式见 [Packages · 清单](../../src/Docs/Orisumi.Docs/content/docs/hosting/packages.mdx#manifest)。
- .NET 的构建输出在 `.artifacts/observers/build/`。
- 步骤和容器镜像见 [Runtime README](../../src/Observers/Orisumi.Observer.Runtime/README.md)。

## 验证

| 命令 | 作用 |
| --- | --- |
| `verify checks [--scope changed\|all]` | 运行构建和自动化检查，`--scope` 选择范围，默认 `changed` |
| `verify scenarios [场景...]` | 运行冒烟场景。不指定时运行全部场景 |

两种范围：

| 范围 | 检查 |
| --- | --- |
| `changed` | 根据相对基点的改动选择检查。无法识别的路径触发全部检查 |
| `all` | 构建 .NET 解决方案，运行模型测试、集成测试和开发工具测试，运行前端的 `npm run verify`，检查并构建文档站；在 macOS 上还运行客户端的 `swift test` 并构建客户端 |

两种范围都只包括构建和自动化检查。冒烟场景由 `verify scenarios` 运行，质量扫描由 `analyze quality` 或 `--quality` 运行。

- `--base` 指定基点。有未提交的改动时可以省略，默认使用 `HEAD`；没有改动时必须指定。
- `--quality` 在同一次运行中执行检查，再以同一基点运行质量扫描，两者共用一份运行记录。
- `--base` 和 `--quality` 只用于 `changed` 范围，与 `--scope all` 一起使用时返回参数错误。
- 构建失败、测试失败、场景失败或扫描未完成时，命令返回失败。质量扫描的发现不使命令失败，供评审参考。
- 构建和自动测试不覆盖容器运行、页面和交互，运行时行为由冒烟场景检查。热更新和页面另外检查。

### 计划与 JSON 输出

加 `--plan` 只输出计划，不执行。加 `--json` 以 JSON 输出：

| 命令 | stdout |
| --- | --- |
| `verify checks --plan --json` | 计划：范围、`quality`、基点、改动路径和步骤 |
| `verify checks --json` | 一个结果对象：`exitCode`、`evidence`、`verification`、`quality` |
| `verify scenarios --plan --json` | 场景计划，包括每个场景的前提是否满足 |
| `verify scenarios --json` | 场景结果 |

`verify checks --json` 的 `evidence` 是证据目录，`verification` 和 `quality` 是这次运行保存的报告，没有生成的报告为 `null`。执行进度写到 stderr，stdout 可以整体解析。取消时同样输出结果，内容是已经完成的部分。

### 冒烟场景

场景对本检出目录的环境执行，检查运行中的服务。每个场景写明前提；前提不满足时，场景报告为跳过并写明原因，跳过不使命令失败。`--plan` 显示每个场景的前提当前是否满足。

| 场景 | 前提 | 检查 |
| --- | --- | --- |
| `environment` | 本检出目录没有运行中的环境 | 用临时卷启动环境：各入口的转发、超过 10 MiB 的请求返回 `413`、只启动部分服务时 `/api` 返回 `503`、迁移失败时 API 不启动、停止后再启动数据仍在。结束时停止环境并删除卷 |
| `api` | 环境运行中，API 已启动，使用临时卷 | 通过 8080 入口提交版本、读回版本，以及提交被拒绝时的响应 |

写入数据的场景要求临时卷，场景数据不进入默认卷。每个 Observer 有一个场景：对环境运行几轮采样，再通过 API 读回结果。依赖本机能力的场景写明要求，例如 macOS。

## 质量与行数

```sh
orisumi analyze quality --base HEAD
orisumi analyze loc
```

两个命令都可以加 `--json`。

`analyze quality` 扫描业务代码、文档站和开发工具的复杂度、重复代码和未使用代码，并与基点比较。发现供审查，业务需要的复杂代码可以保留。基点必须能完成分析构建；历史代码无法编译时，命令报告比较未完成并返回失败，当前代码的扫描结果仍然保存。

终端列出相对基点新增的发现：重复代码、新增或变复杂的函数、未使用的代码，以及未完成的扫描。现有的发现只给数量，完整列表在证据目录的 `quality.json`。`verify checks --quality` 的质量部分写法相同。

`analyze loc` 按模块和语言统计工作区中已跟踪的文件，包括 Swift，不计空行和纯注释行。开头的总数只算代码，也就是实现加测试，实现是生产代码和开发工具。文档、构建描述和生成文件（锁文件、EF 模型快照）单列一行，不进总数。文件先按语言分类，所以测试目录里的 csproj 算构建描述，README 算文档。

`src/` 下的测试归它所在的模块。`tests/` 下的测试项目按 csproj 的 `ProjectReference` 归属：只引用一个模块的归那个模块，引用多个模块或不引用生产项目的归 `Cross-module`。所以 Core 和 Observers 的测试行数很少，它们的测试大多在引用多个模块的 `Orisumi.Integration.Tests` 里。

## 证据

每次验证和质量扫描的证据保存在 `.artifacts/verification/<run-id>/`：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 执行清单 |
| `verification.json` | 验证结果，包括每个场景的通过、失败或跳过及原因 |
| `quality.json` | 质量报告，加 `--quality` 或运行 `analyze quality` 时生成 |
| 步骤日志 | 每个步骤和场景的输出，场景日志包括它发出的请求和收到的响应 |

成功、失败和取消的运行都保存。TUnit 的原始报告在 `TestResults/`，由文档站读取。

```sh
orisumi runs list
orisumi runs prune           # 只预览
orisumi runs prune --apply   # 删除
```

`runs list` 每次运行一行：开始的时间（本地时区）、运行的类型、结果、耗时和失败的步骤，由此可以看出哪次运行失败、失败在哪一步。需要证据目录的路径时加 `--json`。

每次运行结束时自动清理，保留最近 10 次、最近 5 次没有成功的，以及 2 天内的全部运行。`runs prune` 使用同样的条件。质量扫描的基线工作树缓存在 `.artifacts/quality-baselines/<commit>/`，不参与清理，需要回收空间时移除对应的 Git worktree。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 130 | 已取消 |
| 其他非零值 | 执行失败 |
