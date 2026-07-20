# 04: 前端 —— Matcher 提案卡 + NSwag 收编

Status: ready-for-agent

## Parent

[ADR-029](../../../docs/adr/029-observation-depth-matcher.md) §4/§5 + 架构评审候选 ④

## What to build

- **StrandQuestions.vue 改 Matcher 提案卡**:展示锚定 matcher(路径可读渲染,
  如 `Code › 标题含 "hyperframes"`)、时段、AI 名字/释义提案;可编辑字段;
  三出口不变:绑定 / Mute / 跳过(跳过仍是纯客户端,diff 自然续上)。
  提案纪律入 UI 文案:通用工具写进释义,不进指纹。
- **NSwag client 收编知识端点**:起服务器重生成 client,删
  `frontend/src/api/index.ts` 手写的 HandleDto/QuestionItem/BindStrandRequest/
  StrandResponse 及 `as` 裸转 wrapper;时区 offset 传参对齐 recap 端点做法。
- Recap 卡的"知识已更新,重新生成?"提示若 issue 04(strand-knowledge-layer
  旧目录,staleness 读时判脏)已落地,校验其在 matcher 形状下仍工作。

## Acceptance criteria

- [ ] 问题卡渲染 matcher 路径与提案,绑定/Mute 走生成 client,请求形状与服务端 DTO 一致
- [ ] 手写知识类型与 wrapper 删除,NSwag client 含知识端点
- [ ] vue-tsc clean,现有 Dashboard/Recap 功能无回归

## Blocked by

- 03（问题卡 DTO 定形）
