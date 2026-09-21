# Hub 管理与服务器 VRChat 验证

验收快照日期：2026-09-21。实现决策见 [ADR-0019](../adr/ADR-0019-single-owner-hub-management.md)，使用见 [服务器 README](../../src/Server/README.md)。以下结论只对应本次工作树验证，不代表已部署。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| `./scripts/heartbeat-dev verify changed --base cfeb081` | 通过：.NET 测试、Web 类型/lint/格式/单测/生产构建、浏览器测试 | `.artifacts/verification/20260921T014034Z-verify-full-fallback-343dfd119c85497ca108d277f9f16a05/manifest.json` |
| `./scripts/heartbeat-dev scenario hubs-fixture` | 通过：在线操作、状态分离、离线禁用、通用表单清除秘密输入 | `.artifacts/verification/20260921T014419Z-scenario-hubs-fixture-58b35b04c8be4d48af04787d9a52dc53/manifest.json` |
| `dotnet ef migrations has-pending-model-changes --project src/Backend/Heartbeat.Infrastructure --startup-project src/Backend/Heartbeat.Api` | 通过，Initial 与模型无差异 | 本地命令输出 |
| `docker build -f src/Server/Heartbeat.Server/Dockerfile -t heartbeat-server:local-verification .`；隔离容器启动并请求 Hub 状态 | 通过，指定 `Hub__DataDirectory` 后身份、SQLite、锁均在该目录，状态接口正常 | `.artifacts/hub-storage-20260921/server-build.log`、`startup.log`、`result.json` |
| `./scripts/heartbeat-dev quality --base cfeb081` | 通过：以 SDK 升级提交为基线，C# 分析器正常；无新增生产或测试重复片段，无新增或增长的复杂度热点 | `.artifacts/verification/20260921T014311Z-quality-gate-c0b8d6f8e9e548d1ac7c0dbaa32be1fc/manifest.json`、`quality.json` |

贯通集成测试 `ManagedVRChatTests` 覆盖实际 API 命令中继、Hub 管理循环、VRChat Collector、SQLite 接管、后端 PostgreSQL 与回放查询；仅第三方 VRChat API 和测试 Owner 认证是替身。它检查密码和会话不进入 API 报告及公开配置。独立测试覆盖 email OTP 流程、重复 Hub 会话不能覆盖状态、相同 Collector 可由两个 Hub 上报、公开配置不落库、API 重启后的离线展示、每个 Hub 的本地目录隔离、Unix 私有目录权限与重启恢复、配置失败后的暂停持久性，以及 Desktop 本地/远程共用启停入口。

尚未验收：真实 VRChat 登录/TOTP/位置、真实 OIDC 到服务器管理的整条用户链路，以及 AppKit/WinUI 界面运行中的远程启停。浏览器为 fixture，Docker 启动测试使用假凭据和不可用后端，不能替代这些验收。VRChat 使用既有通用 Record 展示，尚无专用 renderer。

`./scripts/heartbeat-dev package desktop` 在 2026-09-21 通过：Mac 宿主显式目标为 `net10.0-macos27.0`，使用 .NET 10 workload 内置的 Xcode 27 预览 SDK pack 完成发布、ad-hoc 签名和签名校验。真实 UI 与权限流程仍待人工验收，见 `.scratch/native-desktop/issues/01-platform-acceptance.md`。

现有数据卷未重建，未部署。按照重写规则只修改 Initial migration；已应用旧 Initial 的开发库需要单独处理。SDK 10.0.401 升级保留，Docker 构建 SDK 已同步。

## 2026-09-21 本地重新启动补验

清空开发数据库和 Hub 队列后，Web/API/服务器 Hub 正常启动并创建当前 Initial 模型；Desktop 和服务器 Hub 均已登记并持续联络。首次 Desktop 启动暴露 `MonoBundle` 原生动态库漏签：外层 `codesign --deep` 校验通过，但进程被 macOS 以 `CODESIGNING Invalid Page` 终止。逐库签名后确认原生窗口打开，随后将逐库签名与校验纳入统一打包入口。

回归测试先失败后通过，并覆盖库签名或校验失败时保留旧产物。验证命令为 `./scripts/heartbeat-dev verify changed --base 488081ad` 和 `./scripts/heartbeat-dev quality --base 488081ad`，均通过，证据分别为 `.artifacts/verification/20260921T015253Z-verify-changed-a05c47e4c9b14fddbb980b860834da26/manifest.json`、`.artifacts/verification/20260921T015306Z-quality-gate-61518a0188be4ae8b8b830f02d5a9da5/manifest.json`。修复后的 Developer CLI `package desktop --output .artifacts/desktop-signing-verification` 通过逐库及整包签名校验，日志为 `.artifacts/desktop-signing-20260921/package.log`；原生完整交互和真实 VRChat 仍待人工验收。
