# 06: 发问 / 整理的流式化（待命）

Status: needs-triage

## Parent

[PRD](../PRD.md) · [ADR-042](../../../docs/adr/042-recap-streaming-generation.md) §8

依赖 [02](./02-streaming-transport-and-generator.md)。**ADR-042 明确不承诺本 issue。**

## Context

发问（`AskingGenerator.cs:85`）与整理提案（`ProposalGenerator.cs:181,199`）与 Recap 共用
`ChatCompletionClient`，同样是同步等待 LLM 的长请求，因此同担 60 秒风险。issue 01 的止血
（nginx 300s 兜底 + 应用侧 120s 显式超时）已经覆盖它们的**可用性**：不会再被换成 HTML 504，
失败会以可读原因返回。

剩下的只是体验差异：这两条路径仍然是"转圈到底"。

## 决策前提

先让 Recap 独自跑一段流式，观察三件事，再决定要不要扩散：

1. 线上链路是否真的通透（首字延迟 ≈ 总时长 = 有人在缓冲，见
   [runbook](../../../docs/runbooks/reverse-proxy.md)）。
2. 15s 心跳 / 90s 首 token / 300s 整段这三个数是否需要调整。
3. 流内 `error` 语义在真实失败中是否够用。

## What to build（若决定要做）

- 两个 generator 各加流式方法，复用 `ChatCompletionClient.CompleteStreamAsync`。
- 但**它们的产物是结构化 JSON（问题列表 / 操作提案），不是散文**——流式对它们的价值远低于
  Recap：部分 JSON 无法渲染。真正可能有意义的形态是"逐个问题/逐条提案"地流，这需要改提示词与
  解析形状，属于新的设计问题，不是本 issue 的机械扩散。

## Comments

- 若结论是"不做"，请直接把 Status 改成 `wontfix` 并写明理由，别让它长期悬着。
