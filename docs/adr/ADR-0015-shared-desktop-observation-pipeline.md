# ADR-0015：桌面观测管线共用，原生读数由平台实现

## 状态：已接受

## 日期：2026-09-20

恢复客户端时，用户确认目标是跨平台基座，先在 Mac 实现和验收，原生采集作为平台实现。已有 Mac 模块同时包含原生 API 和与平台无关的投影、协议及待交接逻辑；直接在其上添加 UI 会让后续平台依赖 Mac 模块或复制采集规则。

将观测模型、Record 投影、标题站稳、物理键位置编号和 Hub 待交接缓冲移入 `Heartbeat.Collector.Desktop`。平台实现通过 `IDesktopObservationSource` 提供规范化的桌面读数，并提供时钟；共享会话显式接收平台的 Collector key。Mac 模块保留原生通知、权限读取、键码转换、连续时钟及独立命令行入口。

本次职责整理不改变现有 Record 协议、Collector key 或 Target，也不新增平台兼容层。Windows 原生采集不在本轮实现范围。独立测试和组合验证继续连接同一份生产实现，遵循 [ADR-0013](ADR-0013-scenarios-compose-implementations.md)。

实现接口与入口见[桌面采集共享实现](../../src/Collectors/Heartbeat.Collector.Desktop/README.md)。
