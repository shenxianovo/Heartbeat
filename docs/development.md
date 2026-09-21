# 本地开发

Heartbeat 使用 Compose 运行 Web、API、PostgreSQL 和 Hub。桌面客户端 Heartbeat Dev 在 macOS / Windows 宿主机运行 AppKit / WinUI、原生 Collector 与进程内 Hub。

## 环境准备

需要 Docker Desktop、Docker Compose 和 `global.json` 指定的 .NET SDK（当前为 10.0.401）。原生桌面客户端另需对应平台 SDK，见下方本地打包。

首次运行独立的服务端 Hub 或原生 Collector 验证场景：

```bash
dotnet run --project tools/Heartbeat.Dev -- env setup
```

`env setup` 是跨平台交互式向导：首次输入 API key 时打开 Auth dashboard，隐藏输入 API key、调用真实 Hub 校验 Owner，再保存 Collector Target 与显示名称。需要交互终端和 Docker Compose，三端使用相同命令。

配置只在全部步骤成功后原子写入 Git 忽略的 `.env.local`；失败、取消、Owner 改变或文件被其他进程修改时保留原文件。发现已有文件时先询问是否更新，默认退出并保留配置；输入 `y` 后重新校验 Auth，各字段可按 Enter 保留已保存的值，已有 API key 时不自动打开浏览器。Hub token 使用独立随机值；敏感值不回显。临时与最终配置在 Unix 上为 `0600`，Windows 上使用仅当前用户的文件 ACL。桌面 UI 的 API key 仍在客户端连接设置中填写并保存到系统凭据库，不要求先运行 `env setup`。

构建与 Auth 校验分别显示阶段、耗时，并每 5 秒报告等待状态；构建上限 10 分钟，校验容器上限 60 秒（内部 Auth HTTP 请求上限 15 秒）。校验失败、超时或取消时清理本次检查容器。Compose 的 Server 入口处理 `--check-auth` 后直接退出，不启动 Hub 服务或打开其数据库。

## 启动

默认启动 Web、API 和 PostgreSQL：

```bash
dotnet run --project tools/Heartbeat.Dev -- env up
```

数据库 Initial migration 成功后 API 才启动。失败时查看 `docker compose logs migrate`。

| 服务 | 宿主地址 |
| --- | --- |
| Web | <http://localhost:3000> |
| API | <http://127.0.0.1:8080> |
| PostgreSQL | `127.0.0.1:54329` |
| Hub | <http://127.0.0.1:4318> |

端口只绑定回环地址。Web 将 `/api/*` 转发到 API。Auth 需允许 `http://localhost:3000` 和回调 `http://localhost:3000/auth/callback`；`localhost` 与 `127.0.0.1` 是不同 origin。

显式选择服务会替换默认组合，并补足依赖：

```bash
dotnet run --project tools/Heartbeat.Dev -- env up api
dotnet run --project tools/Heartbeat.Dev -- env up web hub
dotnet run --project tools/Heartbeat.Dev -- env up hub
dotnet run --project tools/Heartbeat.Dev -- env up desktop
dotnet run --project tools/Heartbeat.Dev -- env up web desktop
```

| 选择 | 实际启动 |
| --- | --- |
| `db` | PostgreSQL |
| `api` | PostgreSQL、migration、API |
| `web` | PostgreSQL、migration、API、Web |
| `hub` | 服务器 Hub，内置 VRChat Collector |
| `desktop` | 构建并打开 Heartbeat Dev（自带 Hub，不启动容器） |
| `web desktop` | PostgreSQL、migration、API、Web，以及 Heartbeat Dev |

Hub 启动不依赖 API 或 PostgreSQL 可用；Web 管理和上传需要 API 恢复。服务器 Collector 的配置步骤见 [服务器 README](../src/Server/README.md)。桌面客户端通过宿主系统打开应用，命令完成后终端即可退出；关闭窗口继续在菜单栏或系统托盘运行，选择“退出 Heartbeat Dev”才停止。首次配置使用上述本地地址；已保存连接时沿用原配置。

`env up desktop` 每次构建本地应用包；已有客户端进程时 macOS 会打开现有实例，Windows 也可能因文件占用无法替换产物。修改客户端代码后，先退出 Heartbeat Dev，再运行启动命令。该入口不使用 `dotnet watch`，macOS 保留应用包身份和权限入口，Windows 保留完整 self-contained 目录。

容器开发模式使用 `next dev` 和 `dotnet watch`。Dockerfile 或依赖变化后，重新运行同一条 `up` 命令。生产镜像验收使用：

```bash
dotnet run --project tools/Heartbeat.Dev -- env up --release
dotnet run --project tools/Heartbeat.Dev -- env up --release hub
```

`--release` 切换容器构建模式，只用于本地验收，要求 Auth 使用 HTTPS；桌面客户端始终通过 DevCLI 打包模块生成本地 Release 应用包。`NEXT_PUBLIC_*` 配置在 Web 镜像构建时写入。

## 本地打包

打包入口统一为 Developer CLI，构建后不启动应用：

```bash
dotnet run --project tools/Heartbeat.Dev -- package desktop
dotnet run --project tools/Heartbeat.Dev -- package desktop --runtime osx-arm64 --output ./out
```

Windows 使用同一组命令：

```powershell
dotnet run --project tools/Heartbeat.Dev -- package desktop --runtime win-x64
```

`--runtime` 默认为宿主机 OS 和架构，可选 `osx-arm64`、`osx-x64`、`win-arm64`、`win-x64`。macOS 包必须在 macOS 上使用匹配的 Xcode 与 .NET macOS workload 构建；Windows 包必须在 Windows 上使用 .NET 与 Windows SDK 构建工具构建。该入口不承诺跨 OS 构建。

`--output` 指定产物的父目录，相对路径基于调用命令时的当前目录。默认目录相对仓库根目录：macOS 为 `.artifacts/desktop/Heartbeat Dev.app`，Windows 为 `.artifacts/desktop-windows/Heartbeat Dev/`，后者必须保留完整目录。

打包先在输出目录的独立临时目录中完成；发布、图标与签名步骤失败或取消时清理临时文件，保留已有产物。成功后只替换本命令的具名应用目录，其他文件保持不变。macOS 对包内原生动态库逐一签名并校验，再签名和校验外层应用包，避免 `MonoBundle` 内的库被 `codesign --deep` 遗漏。开发包固定使用 Bundle ID `com.shenxianovo.heartbeat.desktop.dev` 和本机 `Heartbeat Development` identity；缺失 identity 时打包直接失败，不回退到 ad-hoc。Windows 仍产出 self-contained 应用目录，不提供安装器、发行签名或自动更新。

### 跨平台命令

在仓库根目录使用 `dotnet run --project tools/Heartbeat.Dev -- <子命令>`；macOS、Windows 和 Linux 共用入口、子命令与参数。原生桌面构建仍要求对应 OS 和 SDK。

```powershell
dotnet run --project tools/Heartbeat.Dev -- signing setup
dotnet run --project tools/Heartbeat.Dev -- signing status
dotnet run --project tools/Heartbeat.Dev -- env setup
dotnet run --project tools/Heartbeat.Dev -- env up desktop
```

Windows 的 `signing setup/status` 返回成功并明确说明无需开发签名，不创建证书或修改信任。当前未打包的 WinUI / Win32 / Raw Input 应用不走 macOS TCC 授权；这不代表获得正式发行信任，也不绕过会话隔离、安全桌面或企业策略。Windows 实机验收见[客户端 README](../src/Desktop/README.md#windows-本地应用)。

### macOS 开发签名

每台 Mac 的当前用户首次运行：

```bash
dotnet run --project tools/Heartbeat.Dev -- signing setup
dotnet run --project tools/Heartbeat.Dev -- signing status
```

`setup` 在登录钥匙串创建一个十年有效的 `Heartbeat Development` 自签名 Code Signing identity，已有唯一有效 identity 时直接复用，不修改证书。它只添加当前用户的 Code Signing 信任；macOS 可能要求确认信任或首次私钥访问。各分支和 worktree 共用同一个 identity，日常只运行一个 Heartbeat Dev 实例。

`status` 只读检查登录钥匙串中的 identity，输出证书指纹；缺失、失效或重名返回非零。`setup` 遇到已有但无效的证书也会停止，要求在“钥匙串访问”检查有效期、信任和私钥，避免自动换证书使已有权限失效。正常打包不创建证书、不回退到 ad-hoc，并用唯一指纹选定证书。证书及其私钥必须保留；更换或删除会改变代码身份，需要重新授予 Accessibility 与 Input Monitoring。创建时的临时私钥文件限当前用户访问，成功或失败后均清理；中途已导入的钥匙串条目保留供检查。

开发身份与编译配置独立：DevCLI 显式设置 `HeartbeatDevelopmentBuild=true`，生成 `Heartbeat Dev` / `com.shenxianovo.heartbeat.desktop.dev`，即使使用 Release 编译也仍是开发包。普通项目构建默认是 `Heartbeat` / `com.shenxianovo.heartbeat.desktop`，不引用开发证书。该 identity 不替代 Developer ID、公证或正式发布，仓库目前没有正式发行流程。

首次从旧 Bundle ID 或 ad-hoc 包切换后需要为新的 Heartbeat Dev 授权一次。人工验收：用 `env up desktop` 启动并授权两项权限，退出应用，修改并重新打包，再启动，确认辅助功能和输入监控仍有效。可用 `codesign -d -r- ".artifacts/desktop/Heartbeat Dev.app"` 比较重建前后的 designated requirement；签名一致只证明身份稳定，不替代权限保留验收。不要通过重置 TCC 来验证权限保留。

`env up desktop`（macOS / Windows）与 `scenario desktop-replay` 直接复用同一个打包模块。按功能组织的代码入口与职责见 [DevCLI README](../tools/Heartbeat.Dev/README.md)。

## 状态与清理

```bash
dotnet run --project tools/Heartbeat.Dev -- env logs api
dotnet run --project tools/Heartbeat.Dev -- env status web api db
dotnet run --project tools/Heartbeat.Dev -- env down web
dotnet run --project tools/Heartbeat.Dev -- env down
```

显式选择不会影响其他服务。`down` 移除容器但保留 PostgreSQL、Hub SQLite 和开发缓存卷。

Initial migration 变化或需要清空本地状态时：

```bash
dotnet run --project tools/Heartbeat.Dev -- env reset
dotnet run --project tools/Heartbeat.Dev -- env reset --apply
```

第一条只预览，`--apply` 才删除整个项目的本地数据和缓存卷。

## 配置

| 变量 | 用途 | 默认值或要求 |
| --- | --- | --- |
| `AUTH_AUTHORITY` | API、Web 和 Hub 使用的 Auth 地址 | `https://auth.shenxianovo.com` |
| `AUTH_OIDC_CLIENT_ID` | Web OIDC client ID | `heartbeat-web` |
| `AUTH_OIDC_SCOPE` | Web OIDC scope | `openid profile offline_access` |
| `AUTH_OIDC_AUDIENCE` | API 验证的 OIDC audience | 空 |
| `HEARTBEAT_API_KEY` | Hub 换取后端 access token | Hub 必填 |
| `HEARTBEAT_OWNER_ID` | Hub 所属 Owner UUID | Hub 必填，由 setup 写入 |
| `HEARTBEAT_HUB_TOKEN` | Collector 到 Hub 的本地密钥 | Hub 必填 |
| `HEARTBEAT_HUB_PORT` | Hub 回环端口 | `4318` |
| `HEARTBEAT_COLLECTOR_TARGET` | 独立 Collector 验证场景的稳定 Target | UI 自动读取本机 Target |
| `HEARTBEAT_COLLECTOR_DISPLAY_NAME` | Desktop Collector 展示名 | 可选 |

隔离环境可以指定 Compose 项目名和已有环境文件：

```bash
COMPOSE_PROJECT_NAME=heartbeat-smoke \
  dotnet run --project tools/Heartbeat.Dev -- env up --release --env-file /absolute/path/to/smoke.env
```

统一 CLI 还提供 `verify`、`quality`、`scenario`、`probe` 和 `artifacts`，详见[工程验证](verification.md)。
