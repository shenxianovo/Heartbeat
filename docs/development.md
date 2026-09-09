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

填写 `.env.local` 与 `.local/heartbeat-headless.json` 中的 API key 和 owner `sub`。

无头 Hub 的镜像里不带可选 Collector Package。启动后访问 `/settings/hub`，从 Registry Catalog
选择 Collector 并点击安装；Hub 会自动下载、校验、建立 Runtime Instance 与激活。基础设施配置不再
接受 `instances`、`packageDirectory` 或 Subject ID。细节见
[Headless README](../collection/hub/Heartbeat.Collection.Headless/README.md)。

## 2. 启动

```powershell
./scripts/start-local.ps1
```

macOS/Linux：

```bash
./scripts/start-local.sh
```

`start-local` 使用现有本地数据库，不拉取线上数据；后端启动时会自动执行待应用迁移。
需要检查线上原始快照时，先按[数据刷新步骤](runbooks/refresh-local-data.md)恢复，检查完成后再启动。

打开 <http://localhost:8080>。需要一起启动本地开发 Desktop 时使用：

Windows：

```powershell
./scripts/start-local.ps1 -Desktop
```

macOS：

```bash
./scripts/start-local.sh --desktop
```

开发栈已经运行，只启动或重启客户端：macOS 使用 `./scripts/start-local.sh --desktop-only`，
Windows 使用 `./scripts/start-local.ps1 -DesktopOnly`。此入口不调用 Docker，也不要求 `.env.local`。
Linux 目前只支持开发栈，没有原生 Desktop。

这两个选项都从源码运行客户端并自动打开设置窗口，固定连接 `http://localhost:8080`，使用当前 checkout 的
`.local/desktop` 作为持久 Desktop Profile；不受调用时的工作目录或已有 API 地址环境变量影响。
首次使用需要在开发客户端中配置 API key，鉴权仍走线上 Auth；配置、Collector、缓存与日志保留在
该目录。独立 Profile 可以与日常客户端并行运行，不接管安装版的自启动或更新。
它仍对应本机 Machine，不会创建虚拟设备。

同一 checkout 的开发客户端只能运行一个。再次启动若提示 `The Desktop data directory is already in use.`，
先从已有开发客户端菜单退出，再重启；不要删除锁文件。直接使用 `dotnet run` 时也必须显式隔离目录，例如：

```bash
HEARTBEAT_API_BASE_URL=http://localhost:8080 \
  dotnet run --project collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj \
  -- --data-directory "$PWD/.local/desktop"
```

上面的手动命令需在仓库根目录执行；macOS 默认只驻留菜单栏，点击顶部 Heartbeat 图标打开窗口，
或加环境变量 `HEARTBEAT_SHOW_SETTINGS_ON_START=1` 在启动时打开。仅设置 API 地址不会隔离 Profile，省略 `--data-directory`
会使用日常客户端的默认目录。ExternalHost loopback 监听沿用客户端的端口范围探测；开发 Profile
中的可选 Collector 需要单独安装和连接。

若 token 交换持续超时，可以先对比 IPv4/IPv6 连通性：

```bash
curl -4 --connect-timeout 5 --max-time 12 -o /dev/null -w '%{http_code}\n' https://auth.shenxianovo.com/.well-known/openid-configuration
curl -6 --connect-timeout 5 --max-time 12 -o /dev/null -w '%{http_code}\n' https://auth.shenxianovo.com/.well-known/openid-configuration
```

若只有 IPv6 连接超时，可先退出开发 Desktop，再临时用
`DOTNET_SYSTEM_NET_DISABLEIPV6=1 ./scripts/start-local.sh --desktop-only` 绕行 IPv4。
此变量只影响该次启动的进程，不修改系统网络配置，也不作为脚本默认值；网络路径恢复后去掉它。
Auth 与本地 Analytics 地址是两个独立配置，连接本地 Analytics 仍需访问线上 Auth。

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

跨服务主链路使用独立临时泳道自动验收：

```bash
dotnet run --project tools/Heartbeat.Verification -- run headless-main
# 已登录图形会话中验证 Reference → Desktop → Analytics
dotnet run --project tools/Heartbeat.Verification -- run desktop-main
```

命令读取 `.local/heartbeat-headless.json` 的 API key 与管理身份配置，Auth 使用线上服务；自动构建
并启动 Reference Collector、所选 Headless/Desktop 宿主、Analytics 与独立 PostgreSQL，核对指定 Segment 到达后清理。
每次使用新的数据目录和数据库，报告与日志保留在 `.local/verification/<run-id>/`。它不要求先运行
`start-local`，也不使用或清空现有开发栈数据。参数、断链演练和扩展方式见
[验证命令说明](../tools/Heartbeat.Verification/README.md)。

## 4. 停止

```powershell
docker compose -f compose.local.yml --env-file .env.local down
```

本地数据库位于 `.local/postgres-data`；`down/up` 不会清空它。
开发 Desktop 在启动它的终端前台运行，从客户端菜单退出；关闭 Compose 不会关闭 Desktop，
退出 Desktop 也不会停止 Compose。下次启动会继续使用原来的 `.local/desktop`。

## 按需阅读

- 查询各发布单元最近成功的 Actions 记录：`python3 scripts/release-status.py`，需要已登录的
  GitHub CLI；加 `--json` 输出完整 SHA、run URL 与最近尝试状态。记录表示 workflow 发布／部署
  成功，不证明当前容器 digest 或用户安装版本；现有 Compose 部署仍拉取 `latest`。
- **需要真实历史数据**：使用[本地数据刷新 runbook](runbooks/refresh-local-data.md)。
- **验证历史数据与新客户端写入**：使用 [Local Data Smoke](runbooks/local-data-smoke.md)。
- **修改、发布或诊断 App Catalog**：使用 [App Catalog runbook](runbooks/app-catalog.md)。
- **查看 API 鉴权、调用方和时区约定**：阅读 [API 导读](api.md)。
- **查看数据库设计意图**：阅读 [数据库导读](db.md)。
