# Heartbeat Developer CLI

从仓库根目录运行 `dotnet run --project tools/Heartbeat.Dev -- <子命令>`；macOS、Windows 和 Linux 共用此入口。命令树、参数类型、校验与帮助由 System.CommandLine 声明，各功能的 `CreateCommand()` 注册自己的子命令，`DeveloperCli` 只组合入口。

| 目录 | 命令与职责 |
| --- | --- |
| `Environment/` | `env setup/up/logs/status/down/reset`：Compose 与本地应用启动 |
| `Setup/` | `env setup`：隐藏输入、真实 Auth 校验、Owner 绑定与私密原子保存 |
| `Signing/` | `signing setup/status`：Mac 创建或复用开发签名，Windows 报告无需签名 |
| `Packaging/` | `package desktop`：平台发布、图标、签名、临时目录与产物替换 |
| `Verification/` | `verify changed/full`：Git 变更选择、执行检查 |
| `Quality/` | `quality --base REF`、`quality loc`：质量分析与代码规模 |
| `Scenarios/` | `scenario <name>`：组合真实实现的验收、运行环境与生命周期 |
| `Probes/` | `probe window-title`：真实读数与规则参数评估 |
| `Artifacts/` | `artifacts list/prune/inventory-local`：验证证据、清单与保留策略 |
| `Infrastructure/` | 进程执行、仓库定位 |

各命令将解析结果转为类型化选项后调用执行逻辑。共享逻辑直接接收选项或计划，不重新解析字符串、不递归启动 CLI。`DesktopPackager` 同时供独立打包、环境启动与桌面回放场景使用；平台 SDK 仍负责其原生应用布局。详情见 [ADR-0018](../../docs/adr/ADR-0018-developer-cli-packaging.md)。

分析器的 MSBuild 配置、预算文件、TypeScript 脚本和 `jscpd/` 保留在项目根目录，作为外部工具的固定资源入口。功能目录使用同一个 `Heartbeat.Dev` 命名空间。

命令输入错误返回 2，执行失败为非零；取消返回 130。`Program` 管理取消信号，命令库不施加默认的两秒强制终止时限，使已有证据保存及场景清理得以完成。帮助和解析错误不启动任务子进程；入口仍先定位仓库。

运行方式见[本地开发](../../docs/development.md)；验证证据、敏感数据与保留策略见[工程验证](../../docs/verification.md)。修改命令时覆盖参数路由及有风险的执行边界，修改打包时至少验证失败保留旧产物、取消清理和目标平台的真实 SDK 构建。
