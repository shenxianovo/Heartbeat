# 03: 主射弹 —— 提案 LLM + 确认 UI + recap 反哺,端到端薄线

Status: done

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

## Comments

- 2026-07-19 实现落地(后端可测部分 + 前端待用户端到端验证):
  - **反哺注入**:`RecapProjection.Project` 加 `knownStrands` 映射参,digest 末尾追加"已知脉络"块(HandleDerivation 确定性解析,留投影层);`RecapService.LoadKnownStrandsAsync` 铺 把手→(名,释义) 映射;prompt 模板加"用脉络名称呼"规则(PromptHash 随之变,ADR-023 §4 靠字段可辨)。2 条投影单测。
  - **提案 LLM**:`IProposalGenerator` + `OpenAiCompatibleProposalGenerator`(复用 RecapOptions,裸 HttpClient,一发出 `{name,gloss}`,宽容提取 JSON 围栏)。**失败/未配置一律降级空提案不抛**。
  - **端点**:`GET /api/v1/knowledge/questions?date=`(带时区偏移);`QuestionService.GetDailyQuestionsAsync` 装配 clusters + 提案 → DTO。DTO `QuestionItemResponse`/`DailyQuestionsResponse` 入 Core。
  - **前端**:`StrandQuestions.vue`(owner-only,挂 RecapCard 后)——提案表单卡:改名字/释义、勾/去勾成员、三出口 入库/别再问(Mute)/跳过。`api/index.ts` 手写 `fetchDailyQuestions`/`bindStrand`/`muteHandle` wrapper(questions 带偏移,同 recap 先例)。
  - 验证状态:服务端 101/101 绿,`vue-tsc -b` 通过,全解决方案构建 0 错误。**端到端手测那条待用户在真环境验收**(需配 Recap:LLM + 真采集数据)。
  - 刻意留的薄:提案上下文只喂"把手 + 时长"(页面标题上下文留深化);对话式纠错未做(表单起步);NSwag client 未重生成(端点走手写 wrapper,client 同步留后续)。

## Blocked by

- [01](./01-foundation-model-and-commit.md)
- [02](./02-question-projection.md)
