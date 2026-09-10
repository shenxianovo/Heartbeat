# Collector SDK：Browser 最小 Segment 模块

Status: done

System 和 Browser 的观测模型已验收，先从 Browser 的 Segment 管理提炼 SDK。
本 PRD 只覆盖第一步，不表示完整 Collector SDK 或跨语言协议改造完成。

- [x] Browser 业务只决定活动连续性、读数与观测时间；Segment 身份/起点/轮转由 SDK 管理。
- [x] 通过保存的会话状态恢复事实身份，保留现有状态及快照形状，不引入迁移。
- [x] 复用既有 Revision、Stream、ACK、重试和 outbox 交付，不改协议。
- [x] 108 项测试、构建和 Package 检查通过；本机新包连接、双窗口快照与上传确认已记录。

实施：[01 — Browser 接入](issues/01-browser-segment-sdk.md)。
本轮不做 .NET SDK、Event/Measurement API、独立 npm 包、对象登记或数据库改造。
