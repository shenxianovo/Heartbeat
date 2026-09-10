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

两个入口共用相同的 `--参数` 语法，均需要 Node.js 24+。**不指定项目时启动后端、前端和 Hub；
显式指定项目后，只启动所选项目及必要依赖。**

| 参数 | 启动内容 |
|---|---|
| `--backend` | Analytics 后端，自动带起数据库 |
| `--frontend` | Dashboard 前端 |
| `--hub` | Headless Hub |
| `--desktop` | 当前 checkout 的开发 Desktop |
| `--collector browser` | Browser Collector 开发监听及其 Desktop 宿主 |
| `--stack` | 后端 + 前端 + Hub 的快捷组合 |

```bash
./scripts/start-local.sh --backend                 # 只构建启动后端及数据库
./scripts/start-local.sh --frontend --hub          # 只构建启动前端和 Hub
./scripts/start-local.sh --desktop                 # 只启动桌面，不调用 Docker
./scripts/start-local.sh --stack --desktop         # 三件套 + 桌面
./scripts/start-local.sh --stack --collector browser
```

PowerShell 同样使用双横线，例如 `./scripts/start-local.ps1 --stack --desktop`。
启动前打印所选项目、补齐依赖与实际启动列表；未选择的已有服务不停止、不重建。
原 `--desktop-only` / `-DesktopOnly` 已退役，改用 `--desktop`；旧 `-Desktop` 不再表示“加开桌面”。

本地访问地址（只绑定 loopback）：

| 服务 | 地址 | 启动就绪条件 |
|---|---|---|
| 前端 | `http://localhost:8080` | 页面 `/` 返回 200 |
| 后端 | `http://localhost:18080` | `/health` 返回 200 |
| Hub | `http://localhost:18081` | `/hub/api/v1/collectors` 返回 401/403，说明鉴权入口就绪 |

就绪检查直接访问选中服务，不经过前端代理。单独启动前端并不保证后端 API 或 Hub 管理页面可用；
单独启动 Hub/Desktop 时，后端离线可导致交付等待，本命令不会自动把三件套带起。
前端仍通过同源 `/api/`、`/hub/api/` 路由提供完整页面体验。
`--compose-file PATH`、`--env-file PATH` 和 `--wait-timeout SEC` 可调整 Compose 配置和等待预算，
它们不改变项目选择；自定义 Compose 需为选中服务的容器 8080 端口提供 loopback 映射。
Desktop 固定使用后端的 `18080`，自定义 Compose 若用于 Desktop 联调也需保持这个映射。
已有旧开发栈尚未暴露此端口时，先运行 `start-local.sh --backend`（PowerShell 替换后缀）更新映射。

Desktop 从源码运行并自动打开设置窗口，直连 `http://localhost:18080`，使用当前 checkout 的
`.local/desktop` 作为持久 Profile；不受调用目录或已有 API 地址环境变量影响。
首次使用需要在开发客户端配置 API key，鉴权仍走线上 Auth。配置、Collector、缓存与日志保留在
该目录。独立 Profile 可与日常客户端并行运行，不接管安装版的自启动或更新，仍对应本机 Machine。
Linux 支持开发栈，原生 Desktop/Browser Collector 开发入口需要 macOS 或 Windows。

同一 checkout 的开发客户端只能运行一个。若提示 `The Desktop data directory is already in use.`，
先从已有开发客户端菜单退出，再重启；不要删除锁文件。直接使用 `dotnet run` 时也必须隔离目录，例如：

```bash
HEARTBEAT_API_BASE_URL=http://localhost:18080 \
  dotnet run --project collection/desktop/Heartbeat.Desktop.Mac/Heartbeat.Desktop.Mac.csproj \
  -- --development --data-directory "$PWD/.local/desktop"
```

手动命令需在仓库根目录执行；macOS 默认驻留菜单栏，点击顶部 Heartbeat 图标打开窗口，
或加 `HEARTBEAT_SHOW_SETTINGS_ON_START=1` 在启动时打开。仅设置 API 地址不会隔离 Profile。
开发 Desktop 的 `--development` 将通用 ExternalHost 连接绑定到该 Profile，拒绝旧无绑定入口，
端口被占也不回退；普通生产扩展不能用于这个开发入口。

### Browser 开发与更新

先安装依赖（一次）：`npm ci --prefix collection/collectors/Heartbeat.Collector.Browser`。
已有开发 Desktop 时先从菜单正常退出，再运行：

macOS：

```bash
./scripts/start-local.sh --collector browser
```

Windows / PowerShell：

```powershell
./scripts/start-local.ps1 --collector browser
```

默认打开 Chrome；Edge 加 `--browser-app edge`。Collector 选择自动带起 Desktop 并启用源码监听，
不启动 Docker；需要一起启动三件套时加 `--stack`。`--browser-app` 只选择浏览器应用，不选择 Collector。
sh / pwsh 是统一启动入口，内部复用 Node 的参数解析、启动与就绪检查。

命令复用此 checkout 的 `.local/desktop`，固定连接本地 Analytics，打开独立浏览器用户数据目录。
首次在打开的扩展页启用开发者模式，Load unpacked 选择命令打印的 `.local/browser-development/extension`。
之后源码变更会自动构建、正常停止本次命令拥有的 Desktop、选择新精确包并重启；
开发扩展通过约 30 秒一次的闹钟检查本地构建标识，先持久化当前活动，再自动 Reload 并重连。
已安装旧版开发扩展时，需手动 Reload 一次让自动更新逻辑生效；以后启动或构建更新无需重复点击。
更新保留 Instance、配置、扩展身份和未上传数据；不要卸载重装。旧代码在自动 Reload 完成前离线等待，不宣称新包身份。
扩展被禁用、Profile 不匹配或持久化失败时不会强行 Reload；同一个新构建只自动尝试一次，避免重载循环。
生产扩展不注册开发更新闹钟，也不执行自动 Reload。

命令打印精确包版本/hash，并区分 Desktop 离线、等待扩展和 Ready。Ready 只证明扩展协议连接，
不证明 Analytics 已收到事实。首次仍需为开发 Desktop 配置 API key。默认 Profile 被其他运行者占用时
命令拒绝操作，不停止那个实例；Ctrl+C 正常退出本次 Desktop，浏览器数据保留。

- 内部工具的高级用法：只准备包和绑定、不启动采集，使用 `node scripts/browser-development.mjs --prepare-only`。
- 内部工具选择另一个持久测试 Profile：加 `--profile .local/my-browser-test/desktop`，其扩展与浏览器目录在旁边的
  `desktop-browser/`；同一 Profile 的后续更新继续用相同参数。
- 测试环境无需实际浏览器：`npm --prefix collection/collectors/Heartbeat.Collector.Browser test`；
  启动选择与开发目录切换测试：`node --test scripts/start-local.test.mjs scripts/browser-development.test.mjs`。

实现边界、验收证据及未覆盖项见 [Browser 开发泳道](architecture/browser-development-binding.md)。

若 token 交换持续超时，可以先对比 IPv4/IPv6 连通性：

```bash
curl -4 --connect-timeout 5 --max-time 12 -o /dev/null -w '%{http_code}\n' https://auth.shenxianovo.com/.well-known/openid-configuration
curl -6 --connect-timeout 5 --max-time 12 -o /dev/null -w '%{http_code}\n' https://auth.shenxianovo.com/.well-known/openid-configuration
```

若只有 IPv6 连接超时，可先退出开发 Desktop，再临时用
`DOTNET_SYSTEM_NET_DISABLEIPV6=1 ./scripts/start-local.sh --desktop` 绕行 IPv4。
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
