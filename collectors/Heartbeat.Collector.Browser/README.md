# Heartbeat Browser Collector

浏览器采集器(Chrome MV3 扩展):观测活动标签页,把折叠好的 ActivitySegment 经
loopback 推送给本机 Agent 的 ingest hub。领域角色见 `desktop/CONTEXT.md` 的
Collector 词条与 [ADR-017](../../docs/adr/017-activity-segment-pluggable-collectors.md)。

采集器**不持任何凭证、不知道服务端地址**——离线缓存、鉴权、上传重试全部由
Agent 侧复用(hub 不在线时扩展自己退避重试,队列保留)。

## Build

```powershell
cd collectors/browser
npm install
npm run build      # tsc --noEmit + vite build → dist/
npm run dev        # watch 模式
npm test           # vitest
```

## Load(unpacked)

1. Chrome/Edge 打开 `chrome://extensions`(Edge: `edge://extensions`)
2. 开启 Developer mode
3. Load unpacked → 选择 `collectors/browser/dist/`
4. 本机启动 Heartbeat Agent(hub 随 Agent 运行)

需要 Chrome ≥ 120(MV3 service worker + `AbortSignal.timeout`)。

## Configuration

唯一配置项是 hub 基准端口(默认 `24820`,与 Agent 的 `AgentConfig.IngestPort`
一致),在扩展的 options 页修改,存 `chrome.storage.local`。

基准端口被占时 hub 会向上顺延(范围 10 个端口),扩展凭 `GET /v1/hub` 的身份
应答(`{"app":"heartbeat"}`)在范围内自动定位真正的 hub——不需要手动跟随。

## Behavior notes

- 上报语义见 `src/hub.ts`:4xx = hub 明确拒绝,丢弃不重试;网络错误/5xx =
  Agent 未运行,保留队列退避重试。
- `source = "system"` 是内置采集器的保留名,hub 会拒收——扩展的段一律
  `source = "browser"`。
- IdentityKey 为规范化 URL(origin + pathname,掐 query/fragment;per-domain
  覆写表处理 youtube.com/watch 这类"query 即身份"的站点),原始完整 URL 存
  Attributes。见 `src/normalize.ts` 与 `shared/CONTEXT.md` 的 IdentityKey 词条。

本地端到端验证(不装扩展、手工 POST 模拟)见
[docs/development.md](../../docs/development.md)。
