# Hub 管理与服务器 VRChat 验证

> 2026-09-27 已用登录后自动采集替代通用远程管理，下文 2026-09-21 内容是历史快照，不代表当前入口。下文保留验收当时的命令。旧启动脚本现已删除；重跑时将其前缀替换为 `dotnet run --project tools/Heartbeat.Dev --`。


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

## 2026-09-27 登录后自动采集

当前入口为 Web 选择在线 Hub → 登录 → 按需提交验证码 → 自动采集。已有 Collector 会话失效后，从该账号行重新登录即自动恢复。通用配置、开始、暂停、移除和手填账号 ID 已从 Web 流程与中继协议移除；Desktop 本地启停保持原有责任。

- 浏览器场景先在旧实现失败：缺少登录入口且仍显示生命周期按钮，见 `.artifacts/verification/20260927T004007Z-scenario-hubs-fixture-cc08bc988fff425cb84c0385e45fb2a3/`。完成实现后，`dotnet run --project tools/Heartbeat.Dev -- scenario hubs-fixture` 通过，见 `.artifacts/verification/20260927T005242Z-scenario-hubs-fixture-d4825924ee894f3bbc04d1d77baaee98/manifest.json`，保留验证码表单及恢复采集截图。
- `ManagedVRChatTests` 验证真实 API → Hub 登录转交、第三方账号身份绑定、自动采集 → SQLite 接管 → 上传 PostgreSQL → 回放查询；`VRChatCollectorTests` 验证验证码错误不产生记录、重启自动恢复、失效会话重新登录，以及账号不匹配时保留原会话且不采集。
- Hub 运行模块验证新登录不能被旧验证码会话续接；API 验证跨 Owner 和离线登录被拒绝，登录消息只下发一次并等待 Hub 应答。

浏览器认证/API 为 fixture；贯通集成中的 Owner 认证和第三方 VRChat API 为替身。真实 VRChat 账号、线上 email OTP/TOTP、实际位置和真实 OIDC 到登录的全链路仍需人工验收。本轮没有启动原生 UI、部署或重建用户数据卷。

`dotnet run --project tools/Heartbeat.Dev -- verify closeout --base f7db1ec8` 通过，已核对 `.artifacts/verification/20260927T005334Z-verify-closeout-9f880aade2a143d49c27cec9173366ef/manifest.json`、`closeout.json`、`quality.json`：.NET、DevCLI、前端检查/生产构建、浏览器测试与质量闸门通过；没有新增重复片段、复杂度退化或死码发现。末尾移除无调用的 `LocalSecretStore.Delete` 后另跑 Hub 全部测试通过（`/tmp/heartbeat-auth-hub-final.log`）；该变化不影响接口调用方。

实际开发环境补验（2026-09-27）：用户遇到“登录 macOS 桌面”，真实 API 证实旧 Desktop 进程仍发布原类型列表，新 Web 将其解释成登录能力。仅磁盘重新打包没有更新运行进程。用户退出并重启实际客户端后，复查在线 Desktop 的登录类型为空、服务器 VRChat 登录类型保留；证据 `.artifacts/verification/20260927-desktop-login-live-check.log`。这补上了 fixture 未覆盖的实际运行版本一致性检查，不引入旧协议兼容。
