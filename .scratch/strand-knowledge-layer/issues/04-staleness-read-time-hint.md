# 04: 缓存 staleness —— 读时判脏 + 提示重生成

Status: ready-for-agent

## Parent

[ADR-028](../../../docs/adr/028-strand-knowledge-layer.md)

## What to build

让**已缓存的 recap** 对新知识起反应,兑现"命名 HyperFrames 后,过去的 HyperFrames 日子也该可读"的回溯价值(ADR-028 §6)。核心纪律:**不主动失效,只读时判脏 + 提示,永不自动重生成**——守住 ADR-023 §4。

- **读时判脏(零失效写、零扇出)**:recap 读路径比较 `recap.GeneratedAt` vs 覆盖其把手的 Strand 的 `UpdatedAt`;某 Strand 晚于该 recap 即标记 stale,hint flag 进 recap 响应。检测只是时间戳比较,不写任何失效标记。
- **今天与历史一视同仁**:Strand 变更**只提示、不自动重生成**。ADR-023 原有的"今天按 segment 水位落后 >1h 自动重生成"是**另一路 staleness 信号,不动**。
- **前端**:stale 时在 recap 旁显示"知识已更新,重新生成?"入口,手动触发既有的重生成路径。

## Acceptance criteria

- [ ] 命名一个覆盖历史某日把手的 Strand 后,读那天 recap 出现"重新生成"提示,且**不自动**重生成
- [ ] 点重新生成后,叙事用上新 Strand 名
- [ ] 段水位触发的"今天自动重生成"行为无回归(ADR-023 §4)
- [ ] 无失效写路径:检测仅 `GeneratedAt` vs `UpdatedAt` 一次比较,无扇出

## Blocked by

- [03](./03-proposal-llm-confirm-ui-feedback.md)
