# 桌面采集共享实现

桌面平台共用观测模型、物理键位置协议、连续时间投影、标题站稳和 Hub 待交接缓冲。平台实现通过 `IDesktopObservationSource` 提供读数，通过 `TimeProvider` 提供包含休眠的单调经过时间；宿主将平台的 Collector key 显式传入 `DesktopCollectorSession`。

共享实现不选择操作系统、不调用原生 API，也不默认绑定某个平台的 Collector 身份。macOS adapter 和命令行入口位于 [Mac 模块](../Heartbeat.Collector.Desktop.Mac/README.md)。采集语义仍以 [协议文档](../../../docs/protocols/) 为准，交接语义以 [Hub 契约](../../../docs/hub-record-delivery.md) 为准。

`CollectorOptions` 是现有命令行接入和采样配置，独立运行时仍连接 HTTP Hub。共享会话不管理 Hub 的生命周期。现有投影、缓冲和会话测试继续通过共享实现验证行为；原生测试通过同一观察源接口连接 Mac 实现，测试目录不决定实现的测试粒度。
