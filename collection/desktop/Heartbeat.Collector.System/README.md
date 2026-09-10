# System Collector

平台无关的内置 system Collector。它消费语义化桌面观察，产出 foreground Segment 与
Input Event，并通过 InProcess Collector Protocol 汇入 Hub。

## 目录

- `Collection/SystemCollectorServiceCollectionExtensions.cs`：`AddSystemCollectorInProcessBinding`。
- `Observations/`：平台无关观察模型；Windows/macOS 只实现 adapter。
- `Observations/SystemActivityModel.cs`：前台、away 与标题的业务转场规则，不管理 Fact 身份或交付。
- `Collection/AppMonitorService.cs`：把活动转场转换为现有 Segment，管理修订、定时快照和交付顺序。
- `Collection/SystemCollectorProtocolAdapter.cs`：回调入队与后台交付边界。
- `Collection/SystemInProcessCollector.cs`：InProcess 协议参与者。
- `Input/`、`Package/`：输入事件 seam 与 Package staging 来源。

## 验证与归属

一个活动模型实例观察当前绑定的本机桌面；应用和标题是活动读数，窗口切换是转场证据。
不额外登记窗口或创建持久对象。模型接受的当前活动与最近采样的标题分开：未通过点击门控的
标题变化不会替换活动起始时的标题，也不会切段。定时快照延续当前 Fact，业务转场结束旧活动；
现有超长 Segment 轮转仍由 Fact 输出层处理。

链路：平台回调 → `DesktopObservation` → `SystemActivityModel` → `AppMonitorService` → 现有协议交付。
这一步只覆盖桌面活动，Input Event、协议和存储沿用现状。

```bash
dotnet test collection/desktop/Heartbeat.Collector.System.Tests
```

测试以可控的平台观察和时间覆盖活动规则、快照及协议输出。真机核对时，使用包含本次改动的
Desktop 构建切换应用/窗口、停留超过 30 秒、离开后恢复，确认活动切换与连续时长。
自动测试通过不代表该构建已安装，也不代表已完成原生权限及回调的真机验收。

Package 构建到 `CollectorPackages/System`，随 Windows/macOS Desktop release 交付。领域语义见
[Collection Context](../../CONTEXT.md)，Fact payload 见 [Contracts](../../contracts/README.md)。
