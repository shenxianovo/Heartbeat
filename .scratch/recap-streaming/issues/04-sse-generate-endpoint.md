# 04: POST 流式生成端点

Status: done

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §2/§4/§5/§6/§7

依赖 [02](./02-streaming-transport-and-generator.md)。

## What to build

`POST /api/v1/recaps/daily/generate?date=<DateTimeOffset>`，SSE 响应，owner-only。它就是"显式
重生成"，**不判缓存新鲜度**（判缓存属于 GET）。

### 事件契约

- `event: delta` — 正文增量；
- `event: thinking` — 推理增量（思考模型的思考期只有它，ADR-042 §9）；不进 narrative、不落库；
- `event: done` — 完整的 `DailyRecapResponse`（与 GET 同一个 DTO 形状，前端只有一份渲染逻辑）；
- `event: error` — 可读失败原因；
- `event: ping`，`data: {}` — 心跳，**连接建立即开始，不等上游首 token**，间隔 15s。
  （原计划用 SSE 注释行 `: ka`，但 `SseItem<T>` 输出不了注释行，所以心跳占一个事件类型；
  客户端忽略 `ping` 与任何未知事件类型。）

HTTP 状态码只负责鉴权/参数类 4xx 与互斥 409；生成域的失败全部走流内 `error`（头已发出后 502
不再可能）。

### 时限

用两层链接的 `CancellationTokenSource` 施加（ADR-042 §5）：

- **上游静默 60s**——收到任何块（`Content` 或 `Reasoning`）就 `CancelAfter` 重置。判死线量的是
  "真的没人应答"，不是"还没开始说正文"：思考模型的首个正文 token 实测可达 175s。
- **整段 600s**——兜底，linked 在外层。

两种超时的文案要分开（"连续 N 秒没有任何响应" vs "超过 N 分钟仍未产出完整叙事"），否则错误现场
分不出是上游死了还是活得太久。超时 → `event: error` + 不落库。`HttpClient.Timeout` 必须是
`Timeout.InfiniteTimeSpan`：它覆盖到响应体读完，留着会在读流中途掐断正常的长生成。

### 落库与取消（ADR-042 §6）

- 流中途断开（消费者提前 break）→ 落库那一步永不执行，缓存保持上次成功的正文。
- 最后一个 delta 到手后**立即用独立的 CancellationToken 落库**（不透传请求的 ct），再 yield
  `done`。upsert 的字段与现状一致：`Narrative` / `GeneratedAt` / `Model` / `PromptHash` /
  `SegmentWatermark` / `KnowledgeHash`。
- 空日（零 segments）：不调 LLM，直接 `event: done` 带 `isEmpty=true`，不写缓存。

### 互斥（ADR-042 §7）

进程内按 `(OwnerId, WindowStart)` 加锁；第二个请求**不排队**，直接 409 + 可读原因。不上分布式
锁（backend 单实例），不做 fan-out。

### 实现形状

- `RecapService` 暴露 `IAsyncEnumerable<事件>`：内部累积全文、流末落库。
- Controller 里返回 `TypedResults.ServerSentEvents`（MVC action 返回 `HttpResults` 合法），端点
  **从 OpenAPI 描述中排除**（NSwag 无法为流生成有意义签名），并在注释里写明"响应不是 JSON，
  前端手写 fetch 读流"。
- 端点仍留在 `RecapController`，不为返回类型不同而分裂到 minimal API。

## Tests

- 假生成器吐 3 块 → 收到 3 个 delta + 1 个 done，缓存被写入且 `Narrative` 是拼接全文。
- 首块前失败 / 第 2 块后失败 → 收到 `error`，缓存未被覆盖（上次成功正文仍在）。
- 消费者在第 2 块后 break → 缓存未被写入。
- 生成完成但请求 ct 已取消 → 缓存**已**写入（独立 CT 的断言）。
- 并发两次同一 (owner, 日) → 第二个 409，生成器只被调用一次。
- 空日 → 生成器零调用，done 带 `isEmpty=true`，无缓存行。
- 访客/未认证 → 401/403，生成器零调用。
