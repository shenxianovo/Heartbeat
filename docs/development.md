# Development Guide

这是一条日常开发的最短路径：从本地源码启动 Postgres、后端和前端，再让桌面
Agent 把真实活动上传到本地环境。项目定位与领域术语见
[CONTEXT-MAP](../CONTEXT-MAP.md)。

## 1. 准备环境

本地栈需要 Docker Desktop；直接运行 Agent 和测试还需要项目使用的 .NET、Node.js 工具链。
首次运行时创建本地环境文件：

```powershell
Copy-Item .env.local.example .env.local
```

macOS/Linux：

```bash
cp .env.local.example .env.local
```

`.env.local` 已被 Git 忽略，默认连接真实 Auth 平台；本地仅运行 Postgres、后端和前端。

完成标准：Docker 已启动，仓库根目录存在 `.env.local`。

## 2. 启动本地栈

```powershell
./scripts/start-local.ps1
```

macOS/Linux：

```bash
./scripts/start-local.sh
```

脚本从本地源码构建服务，并等待 <http://localhost:8080> 可访问。后端启动时自动应用
数据库迁移；数据保存在 `.local/postgres-data`，普通的 `down/up` 不会清空它。

完成标准：浏览器打开 <http://localhost:8080> 后能看到 Dashboard。

## 3. 启动桌面 Agent

`HEARTBEAT_API_BASE_URL` 只覆盖当前进程的上传目标，不修改 `config.json`。Auth 仍使用
现有的 `AuthServiceBaseUrl`。

Windows：

```powershell
$env:HEARTBEAT_API_BASE_URL = "http://localhost:8080"
dotnet run --project collection/desktop/Heartbeat.Desktop.Windows
```

macOS：

```bash
export HEARTBEAT_API_BASE_URL=http://localhost:8080
dotnet run --project collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj
```

完成标准：切换窗口或产生输入后，Dashboard 在一个上传周期内显示新的活动。

真实 Browser Collector 的本地运行方式见
[Browser Collector README](../collection/collectors/Heartbeat.Collector.Browser/README.md)。

## 4. 验证改动

运行与改动范围相符的最小测试集；跨项目改动运行完整测试：

```powershell
dotnet test
dotnet test server/Heartbeat.Server.Tests
dotnet test collection/hub/Heartbeat.Collection.Hub.Tests
dotnet test collection/desktop/Heartbeat.Collector.System.Tests
dotnet test collection/desktop/Heartbeat.Desktop.Windows.Tests
dotnet test collection/desktop/Heartbeat.Desktop.Mac.Tests
dotnet test shared/Heartbeat.Core.Tests
```

服务端数据库测试使用 Testcontainers，需要 Docker。Browser Collector 单独运行：

```powershell
cd collection/collectors/Heartbeat.Collector.Browser
npm test
```

如果修改了服务端 DTO 或端点，按 [API 导读](api.md#客户端重新生成) 重新生成客户端并
通过前端类型检查。

完成标准：相关自动化测试通过，并在本地栈完成受影响用户路径的端到端验证。

## 5. 停止或重置

停止容器并保留本地数据：

```powershell
docker compose -f compose.local.yml --env-file .env.local down
```

需要空数据库时，先停止容器，再只删除仓库内的 `.local/postgres-data`：

```powershell
Remove-Item -LiteralPath ./.local/postgres-data -Recurse -Force
```

macOS/Linux：

```bash
rm -rf -- ./.local/postgres-data
```

下次启动时会创建空数据库并应用全部迁移。

## 低频流程

- **需要真实历史数据**：使用[本地数据刷新 runbook](runbooks/refresh-local-data.md)。
- **修改、发布或诊断 App Catalog**：使用 [App Catalog runbook](runbooks/app-catalog.md)。
- **查看 API 鉴权、调用方和时区约定**：阅读 [API 导读](api.md)。
- **查看数据库设计意图**：阅读 [数据库导读](db.md)。
