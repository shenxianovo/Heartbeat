# 02: digest 深度树泛化——声明驱动的分解

Status: ready

## Parent

[ADR-030](../../../docs/adr/030-collector-depth-declaration.md) §2/§7

## What to build

- **RecapProjection 树泛化**:时间骨架(轨/块/会话/碎段合并)不动;system 块内分解
  与插件轨统一为按生效声明的**递归分解**(逐层按首读数值分组、并集时长),预算剪枝
  (展开门槛/子数封顶/尾部折叠)原样复用;缺读数段挂**最深可用读数**,不造假值。
  browser 轨由 Top-30 平铺变为 url → tab_title 两层树(v2 后三层,零改动)。
- **RecurringLabel 退役**:近 14 天高频注释取首层读数值(声明驱动),per-Source
  分支删除。
- **判官 SystemPrompt 词汇生成**:读数词汇段从生效声明渲染(source、层序、读数名),
  硬编码词汇删除。
- **前端标签下发**:DailyQuestionsResponse 附 readingLabels(生效声明的
  name → label 字典);StrandQuestions.vue 的 READING_LABEL 硬编码退役,未知读数
  回落原名。NSwag 重生成。

## Acceptance criteria

- [ ] 树泛化单测:多层递归分解、并集不双计、缺读数挂最深可用、剪枝行为不回归
- [ ] browser 轨断言从平铺迁移到两层树;system 块分解行为不变(digest 文本断言)
- [ ] 判官 prompt 词汇段随声明变化的单测;RecurringLabel 分支删除
- [ ] readingLabels 端到端:响应携带、前端渲染、未知读数回落
- [ ] 服务端套件绿;vue-tsc 干净

## Blocked by

01
