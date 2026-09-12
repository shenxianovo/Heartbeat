# ADR-0001：使用时间有序轨道记录观测

## 状态：已接受

## 日期：2026-09-12

已确认 — 初始化 Clean Room 观测记录模型

## 背景

Heartbeat 需要记录来自桌面采集器、浏览器扩展和账号集成等不同来源的原始活动痕迹，并支持按时间重放。以语义化事实或通用实体关系图作为存储内核，会迫使采集阶段解释“人实际在做什么”，也无法直接回答异构数据应如何排列和重放。

## 决策

使用 `Timeline → Collector → Track → Record` 作为记录模型。Collector 是采集器实现与其 Target 的稳定绑定，不等同于运行进程或安装实例。Track 固定记录类型、协议版本和时间模式；时间模式先区分 Point 与 Range，Range 再区分明确结束与由下一条 Record 推导结束。Record 保存符合 Track 协议的规范化观测值，不保存对人的活动解释。语义分析结果如有需要，也作为派生 Track 写入，不进入记录内核。

## 后果

- ✅ 不同设备、应用和账号来源可以进入同一条时间线，无需共享设备—应用层级。
- ✅ 重放程序可以依据 Track 的时间形状统一排列 Record。
- ✅ 原始记录与后续语义解释相互独立，可以重新分析历史数据。
- ⚠️ 每种 Record 载荷仍需由对应的模式版本定义，并由通用或专用展示器读取。
- ⚠️ 跨 Track 的活动归因和关联需要在后续派生处理中完成。

## 参考

- [`CONTEXT.md`](../../CONTEXT.md) — 领域术语
- [`docs/recording-storage-model.md`](../recording-storage-model.md) — 完整存储结构和约束
- [`src/Backend/Heartbeat.Domain/Recording`](../../src/Backend/Heartbeat.Domain/Recording) — 记录模型
- [`src/Backend/Heartbeat.Infrastructure/Persistence`](../../src/Backend/Heartbeat.Infrastructure/Persistence) — PostgreSQL 持久化映射
