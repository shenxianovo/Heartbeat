# Heartbeat Developer CLI

从仓库根目录运行 `dotnet run --project tools/Heartbeat.Dev -- <命令>`。人和 Agent 共用此验证入口；需要仓库 `global.json` 指定的 .NET SDK。本机文档检查和质量扫描需要 Node、pnpm，集成测试需要 Docker。

## 开发环境

本地服务由 Aspire AppHost 编排，环境启动、停止、资源状态与日志使用 Aspire CLI 和看板。运行说明见 [AppHost README](../Heartbeat.AppHost/README.md)。本工具不提供环境生命周期命令。

## 构建、测试与收口

先安装本机文档依赖与扫描工具：

```sh
pnpm --dir src/Docs/Heartbeat.Docs install --frozen-lockfile
npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts
```

```sh
dotnet run --project tools/Heartbeat.Dev -- verify full
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --plan
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD --plan
dotnet run --project tools/Heartbeat.Dev -- verify closeout --base HEAD
```

`full` 构建 .NET 解决方案，运行集成测试和开发工具测试，再检查、构建文档站。集成测试使用独立 PostgreSQL 测试实例，需要 Docker。

`changed` 根据相对基点的已跟踪改动和未跟踪文件选择检查，无法识别的路径回退到全量。有未提交改动时可以省略基点，默认 `HEAD`；工作树干净时须指定基点。`--plan` 只显示选择结果。

`closeout` 在同次运行中执行变更验证和质量观察。构建、测试失败或扫描未完成返回失败，质量发现本身不使收口失败。容器运行、页面和交互按改动另行验证，证据不互相替代。

## 质量与行数

```sh
dotnet run --project tools/Heartbeat.Dev -- quality --base HEAD
dotnet run --project tools/Heartbeat.Dev -- quality --base HEAD --json
dotnet run --project tools/Heartbeat.Dev -- quality loc
dotnet run --project tools/Heartbeat.Dev -- quality loc --json
```

复杂度、重复代码和疑似未使用代码覆盖业务实现、文档站及开发工具。重复代码分别报告生产、测试与工具；复杂度按模块报告，突出复杂度大于 10 的函数及其变化。必要复杂度可以保留；重复是否值得共享、未使用发现是否真实，都需要结合行为判断。

C# 使用编译器分析器的复杂度、耦合和未使用私有成员诊断；JS/TS 使用 ESLint 复杂度与 Knip 的文件、导出、依赖分析。重复扫描使用 jscpd，最少 8 行、70 个 token。这些扫描不证明全部调用关系或业务价值，也不检查全部 NuGet 依赖是否有用。

基点必须是包含相应源码且能够完成分析构建的 Git 提交。基线和当前代码使用同一套扫描配置；历史源码无法编译时，明确报告比较未完成并返回失败，同时保留已经完成的当前扫描结果。工具版本固定在 `jscpd/package-lock.json`；该目录承载三种 JS 扫描工具。Knip 配置注明框架入口，`jscpd` 由 C# 启动，不由 JS 引用。

`loc` 不执行构建或扫描，统计 Git 跟踪文件与未忽略的新文件，排除空行和纯注释行；按模块、语言、生产、测试、工具、构建、文档、生成文件分类。MDX 属于文档，锁文件与 OpenAPI 属于生成文件，共用测试工具计入 Backend 测试。它是行数观察，复杂字符串和嵌入语言仍可能存在计数误差。

## 证据

验证证据位于 `.artifacts/verification/<run-id>/`，包括执行清单 `manifest.json`、验证结果 `verification.json`、步骤日志，以及质量或收口报告。成功、失败和取消均记录状态。TUnit 原始报告继续位于 `TestResults/`，由文档站读取。

```sh
dotnet run --project tools/Heartbeat.Dev -- artifacts list
dotnet run --project tools/Heartbeat.Dev -- artifacts prune
dotnet run --project tools/Heartbeat.Dev -- artifacts prune --apply
```

每次验证结束自动保留最近 10 次运行、最近 5 次未成功运行（失败、取消或缺失清单），以及最近 2 天内的全部运行，三组取并集。手动清理默认预览，可通过 `--keep`、`--keep-failed`、`--older-than-days` 调整。`artifacts inventory-local` 生成 `.local` 的只读清单。

质量基线工作树缓存位于 `.artifacts/quality-baselines/<commit>/`，供扫描复用，不属于验证历史清理范围。需要回收时，可移除相应缓存的 Git worktree 后重新扫描。

## 实现组织

`Verification` 选择并执行检查，`Quality` 分类和扫描源码，`Artifacts` 保存和管理证据，`Infrastructure` 执行进程并定位仓库。各命令通过 System.CommandLine 注册，解析后直接传递类型化选项，不递归调用 CLI。

输入错误返回 2，执行失败返回非零，取消返回 130。职责决定见 [ADR 0007](../../src/Docs/Heartbeat.Docs/content/docs/adr/0007-developer-cli-and-verification.mdx)。
