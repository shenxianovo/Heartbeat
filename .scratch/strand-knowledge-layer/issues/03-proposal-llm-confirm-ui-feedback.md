# 03: 主射弹 —— 提案 LLM + 确认 UI + recap 反哺,端到端薄线

Status: ready-for-agent

## Parent

[ADR-028](../../../docs/adr/028-strand-knowledge-layer.md)

## What to build

闭合价值回路的 tracer bullet:未解释簇 → 问题 → 用户命名 → recap 用上名字。**刻意薄**——提案质量、对话式纠错、强度精算全部留后续深化(ADR-028 §5 "对话式留门")。

- **提案 LLM(生成层,薄、不可测,ADR-028 §5)**:对 issue 02 产的一个候选簇发**一发** OpenAI 兼容 chat completion,复用 ADR-023 的 `Recap:BaseUrl/ApiKey/Model` 配置与裸 HttpClient。产出 `name` 猜测 + `gloss` 猜测;**成员预勾由投影(02)给,不靠 LLM**。失败降级为空提案(用户纯手填),不写坏数据。
- **端点**:`GET` 当日候选提问(02 投影 + 提案拼装);提交复用 issue 01 的绑定/Mute 端点。
- **前端(表单式确认,ADR-028 §5)**:recap 卡旁挂"AI 有 N 个问题"入口;点开是**提案表单卡**——可改 name/gloss、勾/去勾成员 Handle、**提交 / Mute / 跳过**三出口。薄 UI,不做聊天。
- **recap 反哺注入(投影层,ADR-028 §6)**:Recap 投影解析当日把手 → Strand(锚点主导,确定性),吐带注结构进 prompt,prompt 指示 LLM 用 Strand 名。绑定后**下一条**重新生成的 recap 叙事出现该名字。
- **Dashboard 首度写数据**:确认走 `Dashboard → Analytics` 写路径(CONTEXT-MAP 已认下)。

## Acceptance criteria

- [ ] 端到端手测:一个未命名簇在 recap 旁出现问题;命名后重新生成 recap,叙事出现该 Strand 名(而非罗列 app)
- [ ] 提案 LLM 失败不阻塞:降级空提案,用户可纯手填提交,不写坏数据
- [ ] 提交经 01 端点入库;recap 投影注入生效(system 段标签升级为 Strand 名)
- [ ] Mute / 跳过在 UI 可达且行为正确:Mute 后该把手不再被问(02 diff),跳过则下次再端上来
- [ ] 隐私:提案调用出境数据不超过 recap 已出境范围(把手簇 + 标题,ADR-028 §7)

## Blocked by

- [01](./01-foundation-model-and-commit.md)
- [02](./02-question-projection.md)
