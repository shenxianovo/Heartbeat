# Heartbeat Browser Collector

浏览器采集器(Chrome MV3 扩展):观测活动标签页，通过 loopback ExternalHost Binding
协商 Collector Protocol、打开 browser Segment Stream，并把 Fact 交给本机 Agent。领域角色见 [`collection/CONTEXT.md`](../../CONTEXT.md) 的
Collector 词条与 [ADR-017](../../../docs/adr/017-activity-segment-pluggable-collectors.md)。

采集器**不持任何凭证、不知道服务端地址**——离线缓存、鉴权、上传重试全部由
Agent 侧复用(hub 不在线时扩展自己退避重试,队列保留)。

## Build

```powershell
cd collection/collectors/Heartbeat.Collector.Browser
npm install
npm run build      # tsc --noEmit + vite build → dist/
npm run dev        # watch 模式
npm test           # vitest
```

## Load(unpacked)

1. Chrome/Edge 打开 `chrome://extensions`(Edge: `edge://extensions`)
2. 开启 Developer mode
3. Load unpacked → 选择 `collection/collectors/Heartbeat.Collector.Browser/dist/`
4. 本机启动 Heartbeat Agent(hub 随 Agent 运行)

需要 Chrome ≥ 120(MV3 service worker + `AbortSignal.timeout`)。

## Configuration

唯一配置项是 hub 基准端口(默认 `24820`,与 Agent 的 `AgentConfig.IngestPort`
一致),在扩展的 options 页修改,存 `chrome.storage.local`。

基准端口被占时 hub 会向上顺延(范围 10 个端口),扩展凭 `GET /v1/hub` 的身份与
协议应答(`{"app":"heartbeat","proto":2}`)在范围内自动定位兼容 hub——不需要手动跟随。
新版扩展优先使用 Collector Protocol 的 Spec/Stream/lease/Fact ACK；旧 hub 或旧缓存
仍经 `/v1/segments` legacy adapter 投递。协议请求失败或 Fact 未明确 ACK 时待传队列保留。

## Behavior notes

- 上报语义见 `src/protocol.ts`：只有 `committed`、`duplicate`、`superseded` 会逐 Fact
  删除 outbox；拒收、断连或无法解析 ACK 均保留。`src/hub.ts` 只承担旧请求兼容。
- Activation 使用 45 秒 ACK lease；浏览器退出或 Service Worker 长期不续租后，Hub
  在有界时间内释放 Stream writer，但不会声称自己终止了浏览器。
- Hub 的 `enabled` 是 Desired State。停用后扩展结束本地 fold、停止采集/发布，但保留
  未 ACK outbox；临时断连不会改写这一意图。
- `source = "system"` 是内置采集器的保留名,hub 会拒收——扩展的段一律
  `source = "browser"`。
- 扩展只在能够唯一确认宿主品牌时发送平台无关 `appHint` (`chrome` / `edge` /
  `brave` / `opera` / `vivaldi` / `firefox`)。未知或品牌信号冲突时省略 hint；hub
  会保留段，但不会猜测 App 归属。`win:` / `mac:` AppIdentity 由本机平台 resolver 生成。
- IdentityKey 为规范化 URL(origin + pathname,掐 query/fragment;per-domain
  覆写表处理 youtube.com/watch 这类"query 即身份"的站点),原始完整 URL 存
  Attributes。见 `src/normalize.ts` 与 [`shared/CONTEXT.md`](../../../shared/CONTEXT.md) 的 IdentityKey 词条。

## Test the hub without loading the extension

Agent 运行时，可手工 POST 一个 browser 段验证 loopback ingest、离线缓存和上传链路：

```powershell
$body = @{ segments = @(@{
  id = [guid]::NewGuid(); source = "browser"
  identityKey = "https://example.com/page"; appHint = "edge"
  title = "Example"; startTime = (Get-Date).ToUniversalTime().AddMinutes(-5).ToString("o")
  endTime = (Get-Date).ToUniversalTime().ToString("o")
  attributes = @{ url = "https://example.com/page" }
}) } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri http://127.0.0.1:24820/v1/segments -Method Post `
  -ContentType application/json -Body $body
```

`appHint` 是平台无关的独立 Stream enrichment；它不进入 canonical Fact payload。hub
只在 legacy ActivitySegment 投影中把它解析为本机 AppIdentity。
`source = "system"` 是内置采集器的保留名，外部 Collector 使用时会被拒收。

完成标准：请求返回 `accepted: 1`，且该段在一个上传周期内出现在本地 Dashboard 的 Replay 中。
