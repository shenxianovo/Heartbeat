# 02: 传输层与生成层改流式

Status: done

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §8

## What to build

把 LLM 传输与 Recap 生成层从"一次性返回全文"改为"逐块产出"，保持 ADR-023 §2 的投影/生成分层
不变（投影仍是确定性纯函数，生成层仍然很薄）。

### 传输层

- `ChatCompletionClient` 新增 `CompleteStreamAsync(systemPrompt, userPrompt, ct)` →
  `IAsyncEnumerable<LlmChunk>`：请求体带 `stream: true`，用 `HttpCompletionOption.ResponseHeadersRead`
  拿到响应后按 SSE 逐块解析（.NET 10 的 `System.Net.ServerSentEvents` 可直接用）。
- 块必须**分型**：`LlmChunk(Kind: Content | Reasoning)` 分别承载 `choices[0].delta.content` 与
  `delta.reasoning_content`。思考模型在吐出第一个正文 token 前只有后者（实测可长达 175s），只认
  `content` 就会把思考期当成上游没响应（ADR-042 §9）。提取逻辑留成纯函数 `ExtractChunk` 以便测。
- 思考成本按 `RecapOptions.ReasoningEffort`（`default|low|high|max|none`）映射到上游的
  `reasoning_effort` / `thinking.type=disabled`，流式与非流式请求都带；未知值不传（配置写错只该
  退化成上游默认，不该让功能死）。
- 保留既有 `CompleteAsync`（发问/整理仍在用）。两者**共用同一份** URL 拼接、鉴权、失败收敛：
  未配置 / 上游非 2xx / 响应不可解析一律 `ChatCompletionException`。
- 流式路径的失败可能发生在首块之后：仍抛 `ChatCompletionException`，由上层决定语义（不落库）。
- 时限由调用方用链接的 `CancellationTokenSource` 施加，不写死在传输层：上游静默 60s（收到任何
  块就重置）、整段 600s。
- `HttpClient.Timeout` 改成 `Timeout.InfiniteTimeSpan`。实测它管不到 `ResponseHeadersRead` 之后自己
  读的那段流（所以它不是这次失败的原因），但对非流式的缓冲正文完全有效，而流式化之后没人再盯着
  这个值。非流式的 `CompleteAsync` 自己在内部 link 一个 CTS 施加 300s，调用点（发问 / 整理）零改动。
- 读流中途的 `TaskCanceledException` 要在传输层收敛成 `ChatCompletionException`，否则它冒泡上去会
  被写成与"上游静默"一字不差的超时，错误现场分不出是谁死了。

### 生成层

- `IRecapGenerator.GenerateAsync → Task<string>` 改为 `GenerateStreamAsync(digest, ct)` →
  `IAsyncEnumerable<LlmChunk>`（原样透传传输层的分型，生成层保持薄），**只留流式一个方法**
  （Recap 是唯一消费者，留两条路会漂移）。
- `PromptTemplate` / `TemplateHash` / `Model` / `PromptHash` 语义完全不变——本 issue 不碰提示词，
  `PromptHash` 不应改变（改了就等于让所有旧缓存的来源诊断字段失真）。
- `ChatCompletionException` → `RecapGenerationException` 的转换保持不变。

### 测试改造

- `server/Heartbeat.Server.Tests/Services/RecapServiceTests.cs` 与
  `RecapCorrectionFlowTests.cs` 里的两个 Fake 实现改为 async iterator，并保留原有的
  `Calls` 计数 / `Fail` 开关能力（Fail 应支持"首块前失败"与"若干块之后失败"两种）。

## Tests

- `ExtractContent` 之外新增 delta 解析的纯函数测试：正常分块、`[DONE]`、空 delta、含 `role`
  的首块、上游中途返回错误 JSON。
- 断言 `PromptHash` 未因本次改造变化。
