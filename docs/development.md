# Development Guide

这里只保留日常路径。领域边界见 [CONTEXT-MAP](../CONTEXT-MAP.md)；低频操作见
[Runbooks](runbooks/README.md)。

## 1. 准备

需要 Docker、.NET 10 和 Node.js。首次运行：

```powershell
Copy-Item .env.local.example .env.local
New-Item -ItemType Directory -Force .local
Copy-Item collection/hub/Heartbeat.Collection.Headless/heartbeat-headless.compose.example.json `
  .local/heartbeat-headless.json
```

macOS/Linux：

```bash
cp .env.local.example .env.local
mkdir -p .local
cp collection/hub/Heartbeat.Collection.Headless/heartbeat-headless.compose.example.json \
  .local/heartbeat-headless.json
```

填写 `.env.local` 与 `.local/heartbeat-headless.json` 中的 API key、owner `sub` 和 Subject ID。

## 2. 启动

```powershell
./scripts/start-local.ps1
```

macOS/Linux：

```bash
./scripts/start-local.sh
```

打开 <http://localhost:8080>。需要桌面采集时另启 Agent：

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

Browser Collector 见其 [README](../collection/collectors/Heartbeat.Collector.Browser/README.md)。

## 3. 验证

完整仓库验证入口：

```powershell
dotnet test
npm --prefix collection/collectors/Heartbeat.Collector.Browser test
npm --prefix collection/collectors/Heartbeat.Collector.Browser run build
npm --prefix frontend test
npm --prefix frontend run build
node scripts/collector-contracts.mjs check
```

DTO 或端点变更按 [API 导读](api.md#客户端重新生成) 重新生成客户端。采集、摄入、投影或
持久化变更必须执行 [Local Data Smoke](runbooks/local-data-smoke.md)；容器存活不证明数据正确。

## 4. 停止

```powershell
docker compose -f compose.local.yml --env-file .env.local down
```

本地数据库位于 `.local/postgres-data`；`down/up` 不会清空它。

## 按需阅读

- **需要真实历史数据**：使用[本地数据刷新 runbook](runbooks/refresh-local-data.md)。
- **验证历史数据与新客户端写入**：使用 [Local Data Smoke](runbooks/local-data-smoke.md)。
- **修改、发布或诊断 App Catalog**：使用 [App Catalog runbook](runbooks/app-catalog.md)。
- **查看 API 鉴权、调用方和时区约定**：阅读 [API 导读](api.md)。
- **查看数据库设计意图**：阅读 [数据库导读](db.md)。
