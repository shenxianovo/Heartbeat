# 本地开发

Heartbeat 使用 Compose 运行 Web、API、PostgreSQL 和 Hub。macOS Desktop Collector 在宿主机前台运行，以访问系统观察能力。

## 环境准备

需要 Docker Desktop、Docker Compose 和 .NET SDK 10。Desktop Collector 还需要 macOS。

首次运行 Hub 或 Desktop Collector：

```bash
./scripts/setup.sh
```

脚本验证 Auth 身份，并把本地配置写入 Git 忽略的 `.env.local`。

## 启动

默认启动 Web、API 和 PostgreSQL：

```bash
./scripts/heartbeat-dev env up
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
./scripts/heartbeat-dev env up api
./scripts/heartbeat-dev env up web hub
./scripts/heartbeat-dev env up hub
./scripts/heartbeat-dev env up desktop
```

| 选择 | 实际启动 |
| --- | --- |
| `db` | PostgreSQL |
| `api` | PostgreSQL、migration、API |
| `web` | PostgreSQL、migration、API、Web |
| `hub` | Hub |
| `desktop` | Hub 和宿主前台 Collector |

Hub 不依赖 API 或 PostgreSQL。Desktop Collector 附着当前终端，按 Ctrl+C 停止。

开发模式使用 `next dev` 和 `dotnet watch`。Dockerfile 或依赖变化后，重新运行同一条 `up` 命令。生产镜像验收使用：

```bash
./scripts/heartbeat-dev env up --release
./scripts/heartbeat-dev env up --release hub
```

`--release` 只用于本地验收，要求 Auth 使用 HTTPS。`NEXT_PUBLIC_*` 配置在 Web 镜像构建时写入。

## 状态与清理

```bash
./scripts/heartbeat-dev env logs api
./scripts/heartbeat-dev env status web api db
./scripts/heartbeat-dev env down web
./scripts/heartbeat-dev env down
```

显式选择不会影响其他服务。`down` 移除容器但保留 PostgreSQL、Hub SQLite 和开发缓存卷。

Initial migration 变化或需要清空本地状态时：

```bash
./scripts/heartbeat-dev env reset
./scripts/heartbeat-dev env reset --apply
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
| `HEARTBEAT_COLLECTOR_TARGET` | Desktop Collector 的稳定 Target | Desktop 必填 |
| `HEARTBEAT_COLLECTOR_DISPLAY_NAME` | Desktop Collector 展示名 | 可选 |

隔离环境可以指定 Compose 项目名和已有环境文件：

```bash
COMPOSE_PROJECT_NAME=heartbeat-smoke \
  ./scripts/heartbeat-dev env up --release --env-file /absolute/path/to/smoke.env
```

统一 CLI 还提供 `verify`、`quality`、`scenario`、`probe` 和 `artifacts`，详见[工程验证](verification.md)。
