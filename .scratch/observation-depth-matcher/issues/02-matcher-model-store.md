# 02: Matcher —— 路径谓词模型 + 知识库改形 + 注入换轨

Status: done

## Parent

[ADR-029](../../../docs/adr/029-observation-depth-matcher.md) §1/§3

## What to build

知识库的指纹原子从 Handle 换成 Matcher。**数据不保**(ADR-029 页首知情接受),迁移直接换形。

- **Matcher 值对象(Core DTO + server)**:沿某 Source 深度树的路径谓词——
  各层 `(层, 读数, 谓词, 值)` 合取,谓词 ∈ {等于, 前缀, 包含},单层是退化形态。
- **匹配求值(纯函数)**:`MatcherEval.Hits(读数路径, matcher) → bool`;
  当日命中集:某 Owner 的全部 matcher × 当日 segments 的读数路径。
- **实体/迁移改形**:`StrandHandle` → `StrandMatcher`、`MutedHandle` → `MutedMatcher`
  (matcher 序列化按值存);旧表 drop + 新表 create,一个迁移。
- **KnowledgeService / KnowledgeController / DTO** 换 matcher 形状:绑定幂等收敛、
  Mute 幂等、per-Owner 隔离语义全部保持。
- **Recap 注入换轨**:`LoadKnownStrandsAsync` 改为"matcher 当日命中 → 注入该 Strand
  名字+释义";**删除 TriageDecisions 的 known-gloss 读取路径**(机器知识不入库,
  ADR-029 §1;表本身随 issue 03 拆)。已知脉络块渲染单位换 matcher 命中。

## Acceptance criteria

- [ ] MatcherEval 单测:三种谓词、多层合取、单层退化、跨 Source 不串
- [ ] 绑定/Mute 端点 matcher 形状幂等,跨 owner 拒绝,原 KnowledgeServiceTests 语义迁移
- [ ] 注入:matcher 命中当日才注入;不在场 Strand 不进块;triage-gloss 路径删除
- [ ] 迁移可正向应用,segments/recap 无回归,套件绿

## Blocked by

- 01（digest 树的读数路径是求值输入）

## Comments

- 2026-07-20 落地(含前置拆除,原 issue 03 的拆除清单大半在此片完成——实体改形逼的):删 QuestionProjection/QuestionService/TriageGenerator/HandleDerivation/TriageDecision/StrandHandle/MutedHandle 及其测试与 DTO;新增 MatcherDto(路径谓词,Core)、MatcherNormalizer/MatcherCodec/MatcherEval(纯函数)、StrandMatcher/MutedMatcher 实体、MatcherKnowledgeLayer 迁移(drop 3 表 create 2 表,数据不保已知情);KnowledgeService/Controller 换 matcher 形状(幂等含步骤换序收敛);Recap 注入换 matcher 命中、triage-gloss 路径删除;GET questions 端点随旧管线暂拆,issue 03 重建。测试:MatcherEvalTests 9 新增,Knowledge/RecapProjection 测试迁移;套件 102/102 绿。
