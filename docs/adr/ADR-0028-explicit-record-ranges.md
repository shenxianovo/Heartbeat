# ADR-0028：时间模型只保留 Point 和显式 Range

## 状态：已接受

## 日期：2026-10-01

## 背景

`next_record` 最初用于省略区间结束时间，以后一条记录的开始推导前一条的结束。当前没有协议使用它，派生结束时间也未实现；序列边界、迟到和末条记录等额外规则没有实际需求。用户明确撤回 ADR-0001 在 2026-09-27 对该能力的保留决定。

## 决策

- 时间模式只保留 Point 与 Range。Point 的 `endedAt` 为空；Range 必须提供不早于 `startedAt` 的 `endedAt`，允许零长度区间。
- 删除 `next_record` 及整个 EndMode 概念，不保留单值 `explicit` 字段或兼容入口。
- Collector 声明、Hub 接管与交付、后端存储和查询、前端消费同步使用该契约；重写期直接修改 Initial migration、model snapshot 和 Hub 初始 SQLite 结构。
- Range 仍按 ADR-0002 通过同一 Record 的结束时间延长来续期。该变更不新增更正机制，也不根据相邻记录推断观测连续。

Track 继续表达同一 Collector 的协议与时间模式，不再承担隐式区间的后继选择语义。

## 参考

- [ADR-0001](ADR-0001-time-ordered-observation-tracks.md)
- [存储契约](../recording-storage-model.md)
- [记录接口](../recording-api.md)
