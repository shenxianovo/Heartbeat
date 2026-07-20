# 03: 发问判官 —— 单调用判官 + 旧管线拆除 + LLM 传输合流

Status: ready-for-agent

## Parent

[ADR-029](../../../docs/adr/029-observation-depth-matcher.md) §4 + §7 拆除清单

## What to build

发问改为与叙事吃同一 digest 的第二次独立调用;旧的摊平/分诊管线整体拆除。

- **拆除**:`QuestionProjection`(摊平、哨兵名单、ubiquity 门槛、粗筛)、
  `ITriageGenerator`/`OpenAiCompatibleTriageGenerator`、`TriageDecisions`
  实体 + 表(drop 迁移)、旧 `QuestionService` 管线、相关测试。
- **LLM 传输合流**:抽 `ChatCompletionClient`(URL 拼接、Bearer、choices[0] 提取、
  异常收敛、unconfigured 判定,一处实现);Recap 与发问两个 generator 退成
  prompt 构建 + 解析的纯函数,直接单测(架构评审候选 ①,顺路兑现)。
- **`IAskingGenerator`**:输入 = digest(共享前缀)+ few-shot 裁决日志
  (bind/mute 各若干,空日志即冷启动)+ 确定性注释;输出 ≤3 条问题:
  matcher 提案(可含路径细化)+ 时段 + 一次性名字/释义提案。
  解析宽容;任何失败 → 空结果、不写缓存(偏安静,不假装)。
- **`QuestionService` 重写**:缓存按 (owner, day) + 段水位,失败不写;
  读取时对缓存问题做**确定性 diff 过滤**(锚定 matcher 已被裁决的条目剔除,
  零 LLM 重调);封顶 ≤3 由确定性层裁剪。新增 DailyQuestions 缓存表 + 迁移。
- **端点**:`GET /api/v1/knowledge/questions` 返回 matcher 提案形状的问题卡 DTO。

## Acceptance criteria

- [ ] QuestionProjection / TriageGenerator / TriageDecisions 全部删除,无引用残留
- [ ] ChatCompletionClient 单测(unconfigured/HTTP 错/解析错收敛);两个 generator 的 prompt 构建与解析纯函数有单测
- [ ] FakeAskingGenerator 驱动的 QuestionService 测试:缓存命中不重调、水位过期重调、失败不写缓存、裁决后 diff 过滤、封顶 3
- [ ] 发问 prompt 含 digest 前缀 + 裁决日志 + 注释(快照断言)
- [ ] 套件绿

## Blocked by

- 02（问题卡锚定 matcher 提案;diff 过滤要 matcher 求值）
