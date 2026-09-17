# 本地开发

Heartbeat 使用一套 Compose 服务拓扑支持开发热更新和生产镜像验收。默认开发模式在容器中运行 `next dev` 与 `dotnet watch`；`--release` 使用同一拓扑构建并运行标准生产镜像。

macOS Desktop Collector 必须在宿主机前台运行，以访问系统前台应用信息。启动脚本会先启动它所需的 Hub，再把 Collector 附着到当前终端。

## 环境要求

- Docker Desktop 与 Docker Compose
- 宿主机安装 .NET SDK 10（统一 Developer CLI 本身需要）
- Desktop Collector 另外需要 macOS

Web、API 和数据库使用默认认证地址，可以在没有 `.env.local` 的情况下启动。脚本会创建一个权限为 `0600` 的空文件，以便 Compose 始终显式读取同一个环境文件。

首次运行 Hub 或 Desktop Collector 前执行交互式配置：

```bash
./scripts/setup.sh
```

脚本引导你在 Auth 获取 API key，验证身份，然后将本地配置写入仓库根目录的 `.env.local`。该文件已被 Git 忽略。

## 启动服务

默认启动 Web、API 和 PostgreSQL，数据库迁移会在 API 启动前执行：

```bash
./scripts/heartbeat-dev env up
```

`migrate` 是一次性的数据库初始化容器：执行当前 Initial migration 后退出，API 等它成功退出才启动。迁移失败会返回非零退出码并显示错误；可用 `docker compose logs migrate` 查看原因。若旧重写数据库的表结构与当前 Initial 不一致，按下文的 `reset` 说明清空本地状态后重新启动。

访问地址：

| 服务 | 宿主地址 |
| --- | --- |
| Web | <http://localhost:3000> |
| API | <http://127.0.0.1:8080> |
| PostgreSQL | `127.0.0.1:54329` |
| Hub | <http://127.0.0.1:4318> |

所有端口只绑定宿主回环地址。Web 容器把 `/api/*` 转发到 Compose 网络中的 `http://api:8080`。

浏览器开发时优先使用 <http://localhost:3000>，并确保 Auth 允许 origin `http://localhost:3000` 和回调 `http://localhost:3000/auth/callback`。Next 开发服务器也允许从 `127.0.0.1` 访问；若使用该地址登录，Auth 必须另行登记相同 host 的 origin 与回调，浏览器不会把两种回环 host 视为同一个 origin。

命令末尾可以选择一个或多个服务：

```bash
./scripts/heartbeat-dev env up api
./scripts/heartbeat-dev env up web hub
./scripts/heartbeat-dev env up hub
./scripts/heartbeat-dev env up desktop
```

显式选择会替换默认的 `web api db`，脚本只补足必要依赖：

| 选择 | 实际启动 |
| --- | --- |
| `db` | `db` |
| `api` | `db`、`migrate`、`api` |
| `web` | `db`、`migrate`、`api`、`web` |
| `hub` | `hub` |
| `desktop` | Docker 中的 `hub`，以及宿主前台 Collector |

Hub 不依赖 API 或 PostgreSQL，可以在后端离线时持久接管 Record。未选择的服务不会被启动、停止或重建。

Desktop Collector 附着到当前终端，按 Ctrl+C 停止。开发模式使用 `dotnet watch`；Collector 源码变更后会重启宿主进程。Hub 已接管的 Record 保存在 Docker SQLite 卷中，不受 Collector 重启影响。

## 热更新与生产镜像验收

开发模式挂载源码，并在容器内运行 watcher：

- Web 使用 `next dev`；依赖与 `.next` 输出保存在 Docker 卷中，不写入宿主机的 `node_modules`。
- API 和 Hub 使用 `dotnet watch`；各容器的 `bin/obj` 位于独立 tmpfs，不会把 Linux 构建产物写入 macOS 工作树，也不会彼此冲突。

修改源码后无需重新执行启动命令。若修改 Dockerfile、项目依赖或 npm 依赖，重新运行同一条 `up` 命令以重建选中的开发镜像。

使用生产镜像做本地验收：

```bash
./scripts/heartbeat-dev env up --release
./scripts/heartbeat-dev env up --release hub
```

生产 Web 镜像使用 Next.js standalone 输出；`NEXT_PUBLIC_*` OIDC 配置在镜像构建时写入浏览器资源。开发与 release 模式从同一个 `.env.local` 读取这些值。

本项目重写完成前不部署，`--release` 只用于本地验证可发布镜像。

Release 模式要求 Auth 地址使用 HTTPS。本地 HTTP 模拟认证服务只用于开发模式验收。

## 日志、状态与停止

`logs`、`status` 和 `down` 接受相同的容器服务选择；显式选择不会影响其他服务：

```bash
./scripts/heartbeat-dev env logs api
./scripts/heartbeat-dev env status web api db
./scripts/heartbeat-dev env down web
./scripts/heartbeat-dev env down
```

`down` 停止并移除所选容器，但保留 PostgreSQL、Hub SQLite、Web 依赖和构建缓存卷。Desktop Collector 是宿主前台进程，只能在运行它的终端按 Ctrl+C 停止。

当 Initial migration 改变或需要明确清空所有本地状态时运行：

```bash
./scripts/heartbeat-dev env reset
./scripts/heartbeat-dev env reset --apply
```

第一条命令只输出将删除的内容；第二条才会停止整个 Heartbeat Compose 项目，并删除 PostgreSQL、Hub SQLite 和开发缓存卷。普通 `up`、`down` 不删除卷。

## 配置

`.env.local` 使用以下字段：

| 变量 | 用途 | 默认值/要求 |
| --- | --- | --- |
| `AUTH_AUTHORITY` | API、Web 与 Hub 使用的 Auth 地址 | `https://auth.shenxianovo.com` |
| `AUTH_OIDC_CLIENT_ID` | Web OIDC client ID | `heartbeat-web` |
| `AUTH_OIDC_SCOPE` | Web OIDC scope | `openid profile offline_access` |
| `AUTH_OIDC_AUDIENCE` | API 验证的 OIDC audience | 空 |
| `HEARTBEAT_API_KEY` | Hub 向 Auth 换取后端 access token | Hub 必填 |
| `HEARTBEAT_OWNER_ID` | Hub 所属 Owner UUID | Hub 必填；由 setup 验证并写入 |
| `HEARTBEAT_HUB_TOKEN` | Collector 到 Hub 的本地接入密钥 | Hub 必填；与 API key 分离 |
| `HEARTBEAT_HUB_PORT` | Hub 的宿主回环端口 | `4318`；隔离场景自动选择临时端口 |
| `HEARTBEAT_COLLECTOR_TARGET` | Desktop Collector 的稳定 Target | Desktop 必填 |
| `HEARTBEAT_COLLECTOR_DISPLAY_NAME` | Desktop Collector 展示名称 | 可选 |

Hub 容器内固定使用 `http://api:8080` 作为后端地址，SQLite 固定保存在 `/data/hub.sqlite` 对应的持久卷中。API key 只注入 `Hub__ApiKey`，不会作为 Collector 到 Hub 的接入密钥。

需要隔离验收项目或临时认证服务时，可以沿用 Compose 的标准项目名变量并覆盖环境文件：

```bash
COMPOSE_PROJECT_NAME=heartbeat-smoke \
  ./scripts/heartbeat-dev env up --release --env-file /absolute/path/to/smoke.env
```

自定义环境文件必须已经存在；默认 `.env.local` 缺失时才会由脚本创建为空文件。

## 验证开发入口

统一入口自身的命令选择测试不会运行真实容器：

```bash
dotnet test tests/Heartbeat.Dev.Tests
```

`./scripts/heartbeat-dev` 在 Unix/macOS 上使用，Windows 使用 `scripts/heartbeat-dev.cmd`；两者只负责启动同一个 .NET 10 CLI。日常手动启动与 Agent 验证共用这一入口：`env` 管理运行环境，`verify` 选择测试，`quality` 比较结构质量，`scenario` 运行场景，`probe` 在宿主机上量真实读数（也能从本地数据库导出同样格式的读数），`artifacts` 管理证据。完整命令见[工程验证](verification.md)。

当前开发栈已实测 Hub 在后端离线时接管 macOS Record、后端恢复后写入 PostgreSQL，以及 Next 与 .NET watcher 热更新。「Web 通过真实 API 展示 Record」这一段属于待验证：`.artifacts/verification/` 里没有对应场景的证据，端到端用例走的是 fixture。想认下来就跑一次真实链路并留下证据。
