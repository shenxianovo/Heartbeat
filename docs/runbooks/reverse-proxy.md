# Runbook: 反向代理链路（超时与缓冲）

线上请求要穿过四层，其中**只有最内两层在本仓库里**。这份文档存在的唯一理由：另外两层
（Cloudflare、宿主 Caddy）没有版本控制，而 Recap 的 LLM 生成是本项目唯一会长时间占用一条
HTTP 连接的路径（ADR-023 / ADR-042），每次它出问题都要重新推导一遍这条链路。

## 链路

```
浏览器 ──▶ Cloudflare（橙云代理，heartbeat.shenxianovo.com）
        ──▶ 宿主 Caddy（:443 → localhost:8081）        ← 不在仓库，见下方期望配置
        ──▶ frontend 容器 nginx（:8080，compose 暴露 127.0.0.1:8081）
        ──▶ backend 容器（:8080）
        ──▶ 云端 LLM（OpenAI 兼容 API）
```

## 每层的相关限制

| 层 | 关键参数 | 值 | 归属 |
|---|---|---|---|
| Cloudflare | Proxy Read Timeout（超时报 **524**） | Free/Pro 默认 ~100s，**只看源站多久给出响应头**，首字节一出即不再计时 | CF 控制台，不可调（非企业版） |
| Caddy | `reverse_proxy` 响应超时 | 默认无 | 服务器 `/etc/caddy/Caddyfile` |
| Caddy | `flush_interval` | 默认不做周期 flush；`-1` = 关闭响应缓冲、每次写入立即 flush | 同上 |
| nginx | `proxy_read_timeout` | **默认 60s** → 本仓库显式设为 `300s` | `frontend/nginx.conf` |
| nginx | `proxy_buffering` | 默认 on → 本仓库显式 `off` | 同上 |
| backend | `HttpClient.Timeout`（LLM 出口） | 默认 100s → 显式 `120s` | `server/Heartbeat.Server/Program.cs` |

### 两个反复被搞错的语义

1. **`proxy_read_timeout` 计的是两次成功读之间的间隔，不是整段响应的总时长。** 所以"生成要
   200 秒"本身不违规，"上游连续沉默 60 秒"才违规。这就是 Recap 流式生成必须发 15s SSE 心跳的
   原因——活命靠心跳，不靠 LLM 的吐字节奏。
2. **缓冲不会造成超时。** 任何一层把响应体攒起来，都不影响它自己从上游持续读取（读活性照旧
   重置计时器）。缓冲的后果只有一个：客户端在最后一刻一次性收到全文，流式在体验上白做。
   **推论：缓冲是体验故障，超时是可用性故障，两者不要混在一起排查。**

## 期望的 Caddy 配置（服务器上手工维护）

```caddyfile
heartbeat.shenxianovo.com {
    # SSE（Recap 流式生成，ADR-042）必须逐块下发。现代 Caddy 对 text/event-stream
    # 已按流处理，-1 是显式声明，也是给未来的自己看的：
    # 【不要在这个 site 块里加 encode / gzip】——压缩模块会攒住 SSE。
    reverse_proxy localhost:8081 {
        flush_interval -1
    }
}
auth.shenxianovo.com {
    reverse_proxy localhost:8080
}
```

改完 `caddy reload`（或 `systemctl reload caddy`）。

## 排障

### 症状：`504 Gateway Timeout`，`server: cloudflare`，body 是 HTML

**看耗时**：如果稳定在 60 秒附近，就是 nginx 的默认 `proxy_read_timeout`（本仓库已改，
说明部署的镜像里是旧配置——重新 build/pull frontend 镜像）。

**分清是谁的 504**：`504` 是源站生成后被 CF 原样转发（CF 会把它包装成自己品牌的错误页，
所以 `server: cloudflare` 不等于 CF 是根因）；CF 自己超时会给 **524**，不是 504。

**确认现场**：

```bash
docker compose logs frontend --since 30m | grep -i "timed out"
# upstream timed out (110: Connection timed out) while reading response header from upstream
```

### 症状：流式上线了，但用户仍然是"转圈 → 全文一次出现"

按上面的推论，这是缓冲，不是超时。判据不需要探针，肉眼即可：

- **首字延迟 ≈ 总时长** → 链路上有人在攒（自内向外逐个排除：nginx `proxy_buffering off`
  是否真的进了镜像 → Caddy 是否被加了 `encode` / 缺 `flush_interval -1` → 最后才怀疑 CF）。
- **首字几秒就到，之后持续增长** → 链路通透，流式生效。

本地复现（只覆盖 nginx 那一层，但它是唯一会造成 504 的一层）：用同一份 `frontend/nginx.conf`
代理一个慢响应/滴水响应，观察 `curl -N -w "status=%{http_code} total=%{time_total}\n"`。

### 症状：生成失败但前端只显示"服务器返回错误（504）"

期望行为是 **502 + 可读原因**（ADR-023 §4）。如果拿到 504，说明代理先于应用放手：检查
`HttpClient.Timeout`（120s）是否小于 nginx `proxy_read_timeout`（300s）——这个不变量的方向
不能反。

## References

- [`frontend/nginx.conf`](../../frontend/nginx.conf) — `/api/` 的超时与缓冲
- [`server/Heartbeat.Server/Program.cs`](../../server/Heartbeat.Server/Program.cs) — LLM 出口的显式超时
- [ADR-023](../adr/023-recap-cloud-llm-projection.md) — Recap 的失败语义（502、不写缓存）
- [ADR-042](../adr/042-recap-streaming-generation.md) — 流式生成、心跳、端点按动词拆分
