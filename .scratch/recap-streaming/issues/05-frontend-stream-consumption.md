# 05: 前端流式消费与渲染

Status: done

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §2/§3/§4

依赖 [03](./03-read-only-get-and-stale-hints.md)、[04](./04-sse-generate-endpoint.md)。

## What to build

### API wrapper（`frontend/src/api/index.ts`）

- 新增流式 wrapper：`fetch` + `ReadableStream` 手工解析 SSE，**不用 `EventSource`**（它只能 GET
  且不能带 `Authorization` 头，而认证是 Bearer）。接受 `AbortSignal`。
- 产出四类回调/事件：`delta(text)`、`thinking(text)`、`done(DailyRecapResponse)`、`error(message)`。
  `ping` 与任何未知事件类型必须忽略。
- `fetchDailyRecap` 去掉 `force` 参数（issue 03 已从服务端移除）。
- 401 的刷新重试逻辑要覆盖流式请求（复用 `authHttp` 的 token 注入；流一旦开始就不重试）。

### `RecapCard.vue`

- **读**：进页面/切日期先 GET。三态渲染：`isEmpty` → "这一天没有记录"；`narrative == null` →
  进入生成；有叙事 → 直接渲染。
- **自动生成**：owner 视角下 `narrative == null` 或 `segmentStale` 时自动发起一次生成 POST
  （阈值判断在服务端，前端只看布尔）。`knowledgeStale` 维持现状：只提示，不自动生成。
- **渲染节奏**：增量文本原样追加，段落仍用现有的 `split(/\n+/)` 对累积文本重算（`paragraphs`
  计算属性不用改）。**不做打字机动画**——"日记与档案"的调性不需要表演。
- **思考面板**：正文到达前显示"正在思考"，滚动展示 `thinking` 增量。**固定高度上限 +
  `overflow-y-auto` 是硬要求**——推理流可达上万字符，不加就撑爆卡片。新内容到达自动滚底，但用户
  手动上滚时让位（抢走滚动位置比不自动滚更烦人）。首个正文 delta 一到就隐藏它：叙事是主角，思考
  只是过程。切日期 / 卸载 / 新一次生成 / 失败 / `done` 都要清空。
- **中止**：切日期或组件卸载时 `AbortController.abort()`。在途的流写到已换日期的卡片上是 bug
  不是取舍。
- **按钮**：流进行中禁用"重新生成"（现有 `:disabled="recap.pending.value"` 已是该语义）。
- **错误文案**：来源从 HTTP status 分支改为流内 `error` 事件的 message；保留"失败时不打断阅读、
  上次成功的叙事仍在"的现状。409（并发）显示"这一天正在生成中"。
- `RecapCorrection` 的 `regenerate` 回调改走流式生成，并保留"纠正提交成功但 Recap 未更新"这一
  区分能力（失败必须能被重新抛出）。

## Tests

- `frontend/src/mocks/handlers.ts` 增加流式 mock（可控分块、可控中途失败、可控 409）。
- 组件测试：三态渲染、`segmentStale` 触发自动生成、切日期 abort 后旧流的 delta 不再写入、流内
  error 后保留上次叙事。
- 思考面板测试：`thinking` 渲染、滚动容器存在（`max-h` + `overflow-y-auto`）、自动滚底、用户上滚
  后不强行拉回、首个 delta 后消失、切日期/失败清空。
