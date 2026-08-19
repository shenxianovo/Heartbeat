# ADR-042: Recap 流式生成——端点按动词拆分，生成寿命绑定连接

## Status: Accepted（amends [ADR-023](./023-recap-cloud-llm-projection.md) §4/§5）

## Date: 2026-08-18

## Context

线上 `GET /api/v1/recaps/daily?date=...&force=true` 稳定返回 `504 Gateway Timeout`，body 是一张
HTML 错误页，本地同一套 compose 栈却正常。定位结果：

- 超时稳定在 **60.0 秒**，等于 nginx `proxy_read_timeout` 的默认值。`frontend/nginx.conf` 的
  `location /api/` 从未设过这个参数。用同一份配置代理一个 65 秒的慢响应可稳定复现。
- 线上链路是 Cloudflare → 宿主 Caddy → frontend nginx → backend。Caddy 那层是裸
  `reverse_proxy`（无 `encode`、无响应超时），Cloudflare 的 524 只看源站多久给出响应头——
  所以 60 秒这道墙只有 nginx 一处。看到 `server: cloudflare` 不等于 CF 是根因：源站的 504 会被
  CF 原样转发并包装成品牌错误页。
- 后端 LLM 出口没有显式 `HttpClient.Timeout`（默认 100s），大于 nginx 的 60s，于是**代理总是先
  于应用放手**：ADR-023 §4 精心设计的"502 + 可读原因、不写缓存"永远到不了前端，用户只看到
  一张 HTML 504。
- 本地之所以没事，不是配置不同，而是那次生成只花了约 26 秒——**同一份代码在 60 秒这条线两侧
  的随机分布**，这类故障不会自己稳定下来。

单纯调大 `proxy_read_timeout` 只是把墙搬远：LLM 调用天然可能超过任何固定时限，"多少秒才够"
没有答案。真正要定的是：**一次 Recap 生成的寿命，应该由什么承载。**

考虑过的三种形状：

| | 阻塞 + 调大 timeout | SSE 流式 | 异步任务（202 + 轮询） |
|---|---|---|---|
| 超时上限 | 只是搬远 | 真正消除（靠心跳） | 真正消除（HTTP 秒回） |
| 关页面/断网 | 白烧一次生成 | 白烧一次生成 | 生成照常完成 |
| 领域模型 | 不变 | 不变 | 新增持久的"生成中"状态 |

## Decision

### 1. 流式生成，寿命绑定连接；可用性修复与形状变更分两步落地

Recap 生成改为 SSE 流式。**流式在这里首先是可用性决定**：`proxy_read_timeout` 计的是两次成功
读之间的间隔而非整段响应时长，所以持续吐字（或心跳）的连接可以活得任意久；其次才是产品收益
（等待变成阅读，契合"日记与档案"的调性）。

生成的寿命就是那条连接的寿命：客户端断开 = 这次生成作废。**不引入持久的"生成中"状态**——
单用户自部署、日均生成个位数、单次成本几分钱，为"合上电脑也要算完"付一个新实体 + 迁移 +
后台执行器不成比例（CONTEXT-MAP 定位不变量：单用户前提的决定显式写进 trade-off）。因此
`CONTEXT.md` 不新增词条：生成中只活在连接里，不是领域状态。

落地分两步，第一步独立可发布：
1. **止血**：nginx `proxy_read_timeout 300s` + `proxy_buffering off`、LLM 出口显式
   `HttpClient.Timeout = 120s`、Caddy 显式 `flush_interval -1`。发问/整理两条 LLM 出口在这一步
   免费受益（共享同一份配置与传输层）。
2. **形状变更**：本 ADR 余下各节。

### 2. 端点按动词拆分：GET 只读，POST 才生成

- `GET /api/v1/recaps/daily` 退成**零 LLM、零写库**的纯读：读缓存 + 判空 + 判脏。`force` 参数
  取消。
- 新增 `POST /api/v1/recaps/daily/generate`，SSE 响应。它本身就是"显式重生成"，不判缓存新鲜度。

拆分同时解决三件事：`GET` 带写副作用（烧 token + upsert）的语义污点；"访客永不触发 LLM"的
保证从"靠另一个端点"升级为"靠动词"；以及浏览器 `EventSource` 只能 GET 且不能带
`Authorization` 头的现实约束——前端用 `fetch` + `ReadableStream` 手工读流，Bearer 自然带上。

端点仍留在 `RecapController`（MVC action 返回 `HttpResults` 合法），但从 OpenAPI 描述中排除：
NSwag 无法为流式生成有意义的签名，前端 wrapper 本来就是手写 fetch。

### 3. 三把尺统一成"只提示"，生成只有一个触发口

ADR-023 §4 的 segment 水位（自动重生成）与 ADR-031 的 knowledge hash（只提示）原本是两把形状
不同的尺。GET 不再允许生成后，统一为三个确定性、零 LLM 的读态提示位：

- `isEmpty=true` → 空日；
- `isEmpty=false && narrative == null` → 有数据但从未生成；
- `segmentStale` / `knowledgeStale` → 已有叙事但落后（1 小时水位阈值**仍留在服务端**，防轮询
  烧 token 的护栏不能交给前端）。

前端在 owner 视角下看到 `narrative == null` 或 `segmentStale` 时自动发起一次生成 POST。判脏位
平铺成两个布尔而非 `staleReasons` 数组——两把尺撑不起一个集合。

### 4. 失败语义搬出 HTTP 状态码

长响应的硬约束：响应头一旦发出，502 就不可能了。因此生成域的失败改为流内 `event: error`
（带可读 message），HTTP 状态码只负责鉴权/参数类 4xx。ADR-023 §4 的"失败不写缓存"不变。

事件契约：

- `event: delta` — 正文增量；
- `event: thinking` — 推理增量。思考模型的思考期只有它、一个正文字都还没有（§9）；它不进
  narrative、不落库，前端拿它显示"正在思考"；
- `event: done` — 完整的 `DailyRecapResponse`（含 `generatedAt` / `model` / 判脏位），使
  "GET 命中"与"POST 生成完"收敛到同一个 DTO 形状，前端只有一份渲染逻辑；
- `event: error` — 可读失败原因；
- `event: ping`（`data: {}`）— 心跳。原打算用 SSE 注释行以免占用事件类型，但 .NET 的
  `SseItem<T>` 只能写出带事件类型的帧、输出不了注释行，所以心跳占了一个事件类型。代价是客户端
  必须忽略它——并且必须忽略任何未知事件类型，否则以后加事件就是破坏性变更。

### 5. 时限量的是"静默"，不是"总时长"

| 名目 | 值 | 理由 |
|---|---|---|
| SSE 心跳（`event: ping`） | 15s（连接建立即开始，不等上游首 token） | 活命靠心跳，不靠 LLM 的吐字节奏 |
| 上游静默上限 | 60s（收到任何帧就重置，`thinking` 也算） | 判死线要量"真的没人应答"，而不是"还没开始说正文"（§9） |
| 整段生成上限 | 600s | 兜底。8/7 实测 181s，3000+ 段的日子留 3x 余量 |
| 非流式出口（发问 / 整理） | 300s，由 `CompleteAsync` 内部 CTS 施加 | 它们仍是阻塞式，得自带上限 |
| `HttpClient.Timeout` | `Timeout.InfiniteTimeSpan` | 它只管响应头与缓冲正文，管不到流式那段读；留着只会误伤非流式那头 |
| nginx `proxy_read_timeout` | 300s | 兜底；它量的是两次读的间隔，只需大于心跳与静默上限 |

**代理不变量（修正版）：代理的 read timeout 量的是两次成功读之间的间隔，不是整段响应时长。**
因此它只需大于心跳间隔与上游静默上限，与整段上限（600s）无关——nginx 的 300s > 静默 60s 成立，
一条每 15s 有心跳的连接可以活十分钟而不触发它。把不变量写成"应用侧超时 < 代理侧超时"是把
"间隔"与"总时长"混为一谈；只有阻塞式出口（发问/整理）才需要那个更强的形式。

`HttpClient.Timeout` 这一格是止血步骤（§1）的遗留物，也是这次排障里被实测推翻的一个工作假设。
真 socket 上（.NET 10）：它覆盖"拿到响应头"以及默认 `ResponseContentRead` 下的缓冲正文，而
`ResponseHeadersRead` 之后自己读的那段流**不在它管辖内**——400ms 的 Timeout 掐不断一条拖 1.5 秒的
流。所以它从来不是 8/7 那次失败的原因，判死线才是。它真正的危害在另一头：**非流式出口读的就是
缓冲正文，Timeout 对它完全有效**，而流式化之后没人再盯着这个值。

改成 Infinite 之后，两条路的上限都搬进了应用层，都能注入、都能测：流式靠编排层的两层 CTS，非流式
靠 `CompleteAsync` 自己 link 的 300s。代价是传输层再没有兜底——新加的 LLM 出口忘了带 CTS 就是无限
等。另外，读流中途的 `TaskCanceledException`（代理断链、上游 RST 都长这样）必须在传输层收敛成
`ChatCompletionException`。让它冒泡成 `OperationCanceledException`，编排层就会把它写成与"上游静默"
一字不差的一句超时，而两种病症长得一样是这次排障最贵的一段。

缓冲与超时是两件事：任何一层缓冲响应体都不影响它从上游读取的活性，因此**缓冲只会让流式在体验
上退化成"一次性显示"，不会让 504 回来**。据此不为 Cloudflare 那一层设置上线前的阻断式验证，
改为上线后一次眼球观察（首字延迟 ≈ 总时长 = 有人在攒），判据写进
[reverse-proxy runbook](../runbooks/reverse-proxy.md)。

### 6. 落库与取消：中途断丢弃，完整生成后落库

- 流中途断开 → 不落库（沿用 ADR-023 §4"失败不覆盖上次成功正文"）。
- 最后一个 delta 到手后**立即用独立的 CancellationToken 落库**，不透传请求的 ct，再 yield
  `done`。语义是"钱已经花完了就把货存下来"，而不是"用户手快就烧空气"。

### 7. 同一 (Owner, 日窗口) 生成互斥，撞上给 409

进程内按 `(OwnerId, WindowStart)` 加锁，第二个请求不排队、直接 409 + 可读原因。不上分布式锁
（backend 单实例部署，`compose.yml` 无 replicas）；不做 fan-out 让多个客户端跟随同一条流——为
"同时开两个标签页看同一天"付一个广播器不成比例。

### 8. 接口形状：`IAsyncEnumerable`，且这次只改 Recap 一条出口

`IRecapGenerator.GenerateAsync → Task<string>` 改为流式的 `IAsyncEnumerable<LlmChunk>`——
分型的块（`Content` / `Reasoning`，见 §9），而不是裸 `string`，**只留流式一个方法**（Recap 是它唯一消费者，留两条路就是留漂移）。`RecapService` 对外暴露
`IAsyncEnumerable<事件>`，内部累积全文并在流末落库；Controller 直接映射成 SSE
（`TypedResults.ServerSentEvents`，.NET 10 原生）。

选迭代器而非 `onDelta` 回调，决定性理由是**它天然表达 §6 的语义**：消费者提前 break（客户端
断）→ 落库那一步永不执行；测试侧假一条流就是一个 async iterator。

`ChatCompletionClient` 新增 `CompleteStreamAsync`，与既有 `CompleteAsync` 并存共用同一份
URL/鉴权/异常收敛。**发问（AskingGenerator）与整理（ProposalGenerator）本次不改流式**：它们
同担 60s 风险，但已在止血步骤中被覆盖；流式化各记一条待命 issue。Recap 先独自趟一遍，
blast radius 小一倍。

### 9. 推理模型：思考期不是静默

`deepseek-v4-pro` 默认开着思考模式（effort 默认 `high`）。拿 8/7 的真实 digest（1284 段 →
8489 字符）直连上游实测：

| 配置 | 响应头 | 首个 `delta.content` | 思考量 | 总耗时 |
|---|---|---|---|---|
| `deepseek-v4-pro`（默认 high） | +0.16s | **+175.5s** | 14456 字 `reasoning_content` | 181s |
| 同上 + `reasoning_effort: low` | +0.17s | +17.4s | 1218 字 | 29s |
| `deepseek-chat`（不思考） | +0.19s | +0.5s | 0 | 4.5s |

流一直在吐（9302 帧），只是前 175 秒每帧的 `delta.content` 都是空串、内容全在
`delta.reasoning_content` 里。三条结论落进设计：

1. **"首个正文 token"不是活性信号。** 传输层因此产出分型的块
   `LlmChunk(Kind: Content | Reasoning)`，判死线改成量静默（§5）：收到任何一帧就重置。只认
   `delta.content` 的实现会把 175 秒的思考期当成"上游没响应"，而且**只在数据正常的日子才犯**
   ——空日子（8/12 只有 42 段）思考几秒就出正文，看着一切正常。
2. **思考期该被看见。** reasoning 增量透传成 `event: thinking`，前端固定高度滚动显示。175 秒的
   黑屏和 175 秒的思考流是两种产品；它不进 narrative、不落库，只是过程。
3. **思考成本是配置，不是硬编码。** `Recap__ReasoningEffort`（`default|low|high|max|none`）映射
   到上游的 `reasoning_effort` / `thinking.type=disabled`。默认 `high` = 保持上游默认，把"日记
   式叙事值不值 14000 字推理"留成一次可回滚的配置改动。

顺带修正 Context 里的一条旧结论：8/15 那次线上 504 **不只是** nginx 的 60s。这种规模的 digest
阻塞式生成本来就要约 180 秒，而止血步骤里的 `HttpClient.Timeout = 120s` 对阻塞式出口是真有效的
（那条路读的是缓冲正文）——代理层放宽了，应用层还卡着。这也是它必须让位给 `CompleteAsync` 自带
CTS 的原因（§5）。

## Consequences

- ✅ 60 秒墙从"功能上限"降级为"兜底"：正常路径由 15s 心跳保证读活性，不再靠余量赌 LLM 快。
- ✅ 生成的触发口从两个（GET 的水位自动重生成 + `force`）收敛为一个（POST）。GET 从此可断言：
  不烧 token、不写库。
- ✅ 三把尺形状一致（全部只提示），"什么时候会花钱"变成前端一次显式判断。
- ✅ 失败重新变得可读：应用先于代理放手，前端拿到原因而不是 HTML。
- ✅ 反向代理链路（含仓库外的 Caddy / Cloudflare）第一次被写进版本控制。
- ⚠️ 关闭页面 / 断网 / 切日期会作废一次生成并烧掉 token（单用户前提下的显式取舍；若哪天需要
  "合上电脑也要算完"，正确答案是异步任务，而非把流式的超时调得更大）。
- ⚠️ 生成域的失败不再能用 HTTP 状态码表达，前端必须解析流内 `error` 事件；`RecapCard` 原先按
  502 分支的文案逻辑随之改写。
- ⚠️ 心跳占用了一个事件类型（`ping`）而不是 SSE 注释行——`SseItem<T>` 写不出注释行。客户端因此
  必须忽略 `ping`，并且必须忽略任何未知事件类型：服务端将来新增事件不该让老客户端出错。
- ⚠️ `GET` 的 DTO 新增字段、语义变为三态，NSwag 客户端需重新生成；`POST` 端点游离在 codegen
  之外，靠 `docs/api.md` 与手写 wrapper 维持契约。
- ⚠️ Caddy 的 `flush_interval -1` 与"不要加 encode"是仓库外的约束，只能靠 runbook 提醒。
- ⚠️ 发问/整理仍是阻塞式长请求，只是有了 300s 兜底——它们的流式化是待命 issue，不是本 ADR 的
  承诺。
- ✅ 判死线量静默而非总时长，思考型模型不再被误杀；思考过程本身成了界面的一部分。
- ⚠️ `thinking` 又占一个事件类型。客户端必须忽略未知事件的约束因此从"以后要用"变成"已经在用"。
- ⚠️ 推理流可达上万字符，前端必须固定高度 + 滚动，否则一次生成就撑爆卡片；自动滚底还得在用户
  手动上滚时让位。
- ⚠️ 时限交给 CTS 之后，传输层不再兜底：任何新的 LLM 出口都必须自带 CTS 时限，忘了就是无限等。
- ⚠️ `ReasoningEffort` 是靠上游"未知参数忽略"存活的宽松映射：写错值只会退化成上游默认，不会报错
  ——省了一次启动校验，代价是配置写错很安静。

## References

- [ADR-023](./023-recap-cloud-llm-projection.md) — 被 amend 的原始决定（§4 缓存与失败语义、
  §5 "v1 不做流式"）
- [ADR-031](./031-hierarchical-strand-episode-teaching-loop.md) — knowledge hash 只提示的先例，
  §3 的"三把尺"统一到它的形状
- [ADR-029](./029-observation-depth-matcher.md) — `ChatCompletionClient` 一处实现的来源
- [`server/Heartbeat.Server/Services/RecapService.cs`](../../server/Heartbeat.Server/Services/RecapService.cs) — 编排、缓存与水位
- [`server/Heartbeat.Server/Services/RecapGenerator.cs`](../../server/Heartbeat.Server/Services/RecapGenerator.cs) — 生成层接口
- [`server/Heartbeat.Server/Services/ChatCompletionClient.cs`](../../server/Heartbeat.Server/Services/ChatCompletionClient.cs) — LLM 传输层
- [`server/Heartbeat.Server/Controllers/RecapController.cs`](../../server/Heartbeat.Server/Controllers/RecapController.cs) — 端点
- [`frontend/src/components/RecapCard.vue`](../../frontend/src/components/RecapCard.vue) — 卡片与错误文案
- [`frontend/nginx.conf`](../../frontend/nginx.conf) — 超时与缓冲
- [Runbook: 反向代理链路](../runbooks/reverse-proxy.md) — 仓库外两层的期望配置与排障判据
- [DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/) — `thinking` /
  `reasoning_effort` 的官方语义，§9 的配置映射据此
