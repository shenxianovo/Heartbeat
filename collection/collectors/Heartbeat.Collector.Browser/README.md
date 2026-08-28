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
cd ../../..
node scripts/collector-contracts.mjs stage browser .local/browser-package
```

## Load(unpacked)

1. Chrome/Edge 打开 `chrome://extensions`(Edge: `edge://extensions`)
2. 开启 Developer mode
3. Load unpacked → 选择 `.local/browser-package/browser-extension/`
4. 本机启动 Heartbeat Agent(hub 随 Agent 运行)

需要 Chrome ≥ 120(MV3 service worker + `AbortSignal.timeout`)。

## Configuration

唯一配置项是 hub 基准端口(默认 `24820`,与 Agent 的 `AgentConfig.IngestPort`
一致),在扩展的 options 页修改,存 `chrome.storage.local`。

基准端口被占时 hub 会向上顺延(范围 10 个端口)，扩展凭 binding 专属的
`GET /v1/collector-protocol/browser` 在范围内定位兼容 hub。握手、Spec、Stream、lease、
Fact ACK 与 Gap 全部使用 Collector Protocol v1；不存在 `/v1/segments` fallback。
协议请求失败或 Fact 未明确 ACK 时待传队列保留。

## Behavior notes

- 上报语义见 `src/protocol.ts`：只有 `committed`、`duplicate`、`superseded` 会逐 Fact
  ACK；永久 `rejected` 会保留到有界 dead-letter 诊断区，`retry` 遵守 Hub 的
  `retryAfterMs`，断连或无法解析 ACK 则以同一 messageId 和原始批次重放。
- durable outbox 有明确容量上限，绝不为新数据驱逐未 ACK 项；超出的观测合并成
  持久 `stream.gap(buffer_overflow)`，在恢复连接后先于后续 Fact 交付。
- Activation 使用 45 秒 ACK lease；浏览器退出或 Service Worker 长期不续租后，Hub
  在有界时间内释放 Stream writer，但不会声称自己终止了浏览器。
- 扩展首次运行生成 `externalHostIdentity` 并持久化到 `chrome.storage.local`。同一 App
  下不同 Profile/安装拥有独立 Stream 与重传连续性；清理扩展数据或重装会形成新 Host。
- Hub 的 `enabled` 是 Desired State。停用后扩展结束本地 fold、停止采集/发布，但保留
  未 ACK outbox；临时断连不会改写这一意图。最近一次 collection policy 持久化在
  `chrome.storage.local`，浏览器重启不会在已知停用时先误采集。
- `src/delivery.ts` 是 Browser-specific deep module；`background.ts` 只通过
  `policy / enqueue / deliveryCycle` interface 接线。Chrome storage 与 loopback HTTP
  是 internal seams，生产使用真实 adapters，测试使用内存 adapters。
- 扩展只在能够唯一确认宿主品牌时发送平台无关 `appHint` (`chrome` / `edge` /
  `brave` / `opera` / `vivaldi` / `firefox`)。缺失或不稳定的 hint 无法形成 App Instance，
  Hub 会拒绝 Activation；稳定但平台暂时无法解析的 slug 仍形成独立 Instance，不猜成 Chrome。
  `win:` / `mac:` AppIdentity 由本机平台 resolver 生成。
- IdentityKey 为规范化 URL(origin + pathname,掐 query/fragment;per-domain
  覆写表处理 youtube.com/watch 这类"query 即身份"的站点),原始完整 URL 存
  Attributes。见 `src/normalize.ts` 与 [`shared/CONTEXT.md`](../../../shared/CONTEXT.md) 的 IdentityKey 词条。

## Verification

`npm test` 覆盖 Browser producer、Host identity、outbox 与协议 transcript；Hub 侧
`BrowserExternalHostProtocolHandlerTests` 覆盖 hello → schema validation → projector。
不能用手工 `/v1/segments` POST 绕过协议验证。
