# 本地开发

Heartbeat 使用 Compose 运行 Web、API、PostgreSQL 和服务器 Hub。桌面客户端 Heartbeat Dev 在 macOS / Windows 宿主机运行原生 UI、Collector 与进程内 Hub。

## 环境准备

需要 Docker Desktop、Docker Compose 和 `global.json` 指定的 .NET SDK。原生客户端还需要对应平台 SDK。

首次运行服务器 Hub 或独立 Collector 场景时配置本地环境：

```bash
dotnet run --project tools/Heartbeat.Dev -- env setup
```

向导验证 API key 与 Owner，并把服务器 Hub 和 Collector 配置写入 Git 忽略的 `.env.local`。桌面客户端在自身连接设置中保存凭据，不依赖 `env setup`。

## 启动

默认启动 Web、API 和 PostgreSQL：

```bash
dotnet run --project tools/Heartbeat.Dev -- env up
```

| 服务 | 宿主地址 |
| --- | --- |
| Web | <http://localhost:3000> |
| API | <http://127.0.0.1:8080> |
| PostgreSQL | `127.0.0.1:54329` |
| Hub | <http://127.0.0.1:4318> |

Web 将 `/api/*` 转发到 API。Auth 需允许来源 `http://localhost:3000` 和回调 `http://localhost:3000/auth/callback`。

显式选择服务会替换默认组合，并自动补足依赖：

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
| `hub` | 服务器 Hub 及其 Collector |
| `desktop` | 构建并打开 Heartbeat Dev，不启动容器 |
| `web desktop` | Web 默认组合及 Heartbeat Dev |

容器开发模式使用 `next dev` 和 `dotnet watch`。`env up desktop` 每次重新打包；修改客户端代码后先退出已运行的 Heartbeat Dev。关闭客户端窗口只隐藏界面，从菜单栏或系统托盘选择退出才停止进程。

本地验收生产镜像：

```bash
dotnet run --project tools/Heartbeat.Dev -- env up --release
dotnet run --project tools/Heartbeat.Dev -- env up --release hub
```

`--release` 只切换容器构建模式并要求 Auth 使用 HTTPS；桌面开发包始终由 Developer CLI 生成。

## 本地打包

只构建、不启动客户端：

```bash
dotnet run --project tools/Heartbeat.Dev -- package desktop
dotnet run --project tools/Heartbeat.Dev -- package desktop --runtime osx-arm64 --output ./out
dotnet run --project tools/Heartbeat.Dev -- package desktop --runtime win-x64
```

`--runtime` 默认为宿主机 OS 和架构，可选 `osx-arm64`、`osx-x64`、`win-arm64`、`win-x64`。打包必须在目标 OS 上执行：macOS 需要匹配的 Xcode 与 .NET macOS workload，Windows 需要 .NET 与 Windows SDK 构建工具。

默认产物为 `.artifacts/desktop/Heartbeat Dev.app` 或 `.artifacts/desktop-windows/Heartbeat Dev/`。Windows 产物是完整的 self-contained 目录；当前不提供安装器、发行签名或自动更新。打包职责见 [ADR-0018](adr/ADR-0018-developer-cli-packaging.md)。

### macOS 开发签名

每台 Mac 的当前用户首次运行：

```bash
dotnet run --project tools/Heartbeat.Dev -- signing setup
dotnet run --project tools/Heartbeat.Dev -- signing status
```

Heartbeat Dev 使用登录钥匙串中的固定 `Heartbeat Development` 身份；缺失、失效或重名时打包失败，不回退到 ad-hoc 签名。更换或删除证书会改变代码身份，需要重新授予 Accessibility 与 Input Monitoring。Windows 上这两个命令会明确说明不需要开发签名。

开发包身份为 `Heartbeat Dev` / `com.shenxianovo.heartbeat.desktop.dev`，与 Debug/Release 优化配置无关。它不替代 Developer ID、公证或正式发布。签名权威与验收边界见 [ADR-0020](adr/ADR-0020-stable-macos-development-signing.md)。开发凭据文件与普通 Keychain 的区分见 [ADR-0022](adr/ADR-0022-macos-development-credentials.md)。

## 状态与清理

```bash
dotnet run --project tools/Heartbeat.Dev -- env logs api
dotnet run --project tools/Heartbeat.Dev -- env status web api db
dotnet run --project tools/Heartbeat.Dev -- env down web
dotnet run --project tools/Heartbeat.Dev -- env down
dotnet run --project tools/Heartbeat.Dev -- env reset
dotnet run --project tools/Heartbeat.Dev -- env reset --apply
```

`down` 移除容器但保留数据卷。`env reset` 默认只预览，`--apply` 才删除本项目的本地数据和缓存卷。

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
| `HEARTBEAT_COLLECTOR_TARGET` | 独立 Collector 场景的稳定 Target | 场景必填 |
| `HEARTBEAT_COLLECTOR_DISPLAY_NAME` | Collector 展示名 | 可选 |

隔离环境可以指定 Compose 项目名和已有环境文件：

```bash
COMPOSE_PROJECT_NAME=heartbeat-smoke \
  dotnet run --project tools/Heartbeat.Dev -- env up --release --env-file /absolute/path/to/smoke.env
```

验证、质量、场景、探针和证据管理见[工程验证](verification.md)。Developer CLI 的代码职责见 [DevCLI README](../tools/Heartbeat.Dev/README.md)。
