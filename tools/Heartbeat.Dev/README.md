# Heartbeat.Dev

Heartbeat.Dev 是人和 Agent 共用的验证工具，负责构建、测试、质量扫描和验证证据。为什么由它组织验证，见 [ADR · 开发验证与测试证据](../../src/Docs/Heartbeat.Docs/content/docs/adr/verification.mdx)。

本地环境的启动和停止由 Aspire 负责，见 [AppHost README](../Heartbeat.AppHost/README.md)。Heartbeat.Dev 不提供环境命令。

## 准备

需要 `global.json` 指定的 .NET SDK。文档站、前端检查和质量扫描还需要 Node.js、npm 和 pnpm，集成测试需要 Docker。

首次使用时，在仓库根目录安装文档站、前端和扫描工具的依赖：

```sh
pnpm --dir src/Docs/Heartbeat.Docs install --frozen-lockfile
npm --prefix src/Frontend/Heartbeat.Web ci
npm --prefix tools/Heartbeat.Dev/jscpd ci --ignore-scripts
```

所有命令都在仓库根目录运行，格式是 `dotnet run --project tools/Heartbeat.Dev -- <命令>`。

## 验证

```sh
dotnet run --project tools/Heartbeat.Dev -- verify full
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD
dotnet run --project tools/Heartbeat.Dev -- verify changed --base HEAD --quality
```

| 命令 | 作用 |
| --- | --- |
| `verify full` | 构建 .NET 解决方案，运行模型测试、集成测试和开发工具测试，运行前端的 `npm run verify`，检查并构建文档站 |
| `verify changed` | 根据相对基点的改动选择检查。无法识别的路径触发全量检查 |
| `verify changed --quality` | 在同一次运行中执行变更检查，再以同一基点运行质量扫描 |

- 加 `--plan` 只显示会运行哪些检查，不执行。加 `--json` 以 JSON 输出计划。
- 有未提交的改动时，可以省略 `--base`，默认使用 `HEAD`。没有改动时，必须指定 `--base`。
- 构建失败、测试失败或扫描未完成时，命令返回失败。质量扫描的发现不使命令失败，供评审参考。
- 构建和自动测试不覆盖容器运行、页面和交互。这些改动需要另外验证。

## 质量与行数

```sh
dotnet run --project tools/Heartbeat.Dev -- quality --base HEAD
dotnet run --project tools/Heartbeat.Dev -- loc
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
| `verification.json` | 验证结果 |
| `quality.json` | 质量报告，加 `--quality` 或运行 `quality` 时生成 |
| 步骤日志 | 每个步骤的输出 |

成功、失败和取消的运行都保存。TUnit 的原始报告在 `TestResults/`，由文档站读取。

```sh
dotnet run --project tools/Heartbeat.Dev -- artifacts list
dotnet run --project tools/Heartbeat.Dev -- artifacts prune           # 只预览
dotnet run --project tools/Heartbeat.Dev -- artifacts prune --apply   # 删除
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
| `Verification` | 选择和执行检查 |
| `Quality` | 分类和扫描源码 |
| `Artifacts` | 保存和管理证据 |
| `Infrastructure` | 运行进程，定位仓库 |

命令用 System.CommandLine 注册，解析后把类型化的选项直接传给实现，不递归调用 CLI。
