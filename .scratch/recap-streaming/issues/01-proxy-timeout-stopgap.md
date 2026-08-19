# 01: 反向代理与超时止血

Status: done（服务器侧 Caddy 一行由 owner 手工执行）

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §1/§5

## What to build

不改变任何形状，只让"慢"不再等于"HTML 504"，并让失败重新变得可读。可独立发布。

### 仓库内

- `frontend/nginx.conf` 的 `location /api/`：`proxy_read_timeout 300s`、`proxy_buffering off`，
  并注释清楚两件事——该参数计的是两次成功读之间的间隔而非总时长；缓冲开着不会超时但会攒住
  SSE。
- `server/Heartbeat.Server/Program.cs`：LLM 出口 `HttpClient.Timeout = 120s`。
  **不变量：应用侧超时 < 代理侧超时**，方向反了失败就会被换成 HTML 504。
- `docs/runbooks/reverse-proxy.md`：CF → Caddy → nginx → backend 全链路、每层参数与归属、两个
  反复搞错的语义、504 与 524 的区分、"缓冲是体验故障 / 超时是可用性故障"的排障判据。
  `docs/runbooks/README.md` 补索引。

### 服务器（不在仓库，owner 手工）

```caddyfile
heartbeat.shenxianovo.com {
    reverse_proxy localhost:8081 {
        flush_interval -1
    }
}
```

配 `caddy reload`。同时记住：**这个 site 块不要加 `encode`/gzip**，压缩模块会攒住 SSE。

## Tests

- 用同一份 `frontend/nginx.conf` 在本地 docker 里代理一个 65s 慢响应，`curl -N -w
  "status=%{http_code} total=%{time_total}\n"` 应当拿到 200 而不是 60s 的 504。
- 让 LLM 出口人为超时（错的 BaseUrl / 极小 timeout），确认前端拿到 502 + 可读原因，而不是 HTML。

## Comments

- 发问（`AskingGenerator`）与整理（`ProposalGenerator`）共享同一份 nginx 配置与
  `ChatCompletionClient`，本 issue 让它们免费获得同样的兜底。
