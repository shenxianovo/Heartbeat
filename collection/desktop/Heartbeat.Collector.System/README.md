# System Collector

平台无关的内置 system Collector。它消费语义化桌面观察，产出 foreground Segment 与
Input Event，并通过 InProcess Collector Protocol 汇入 Hub。

## 目录

- `Collection/SystemCollectorServiceCollectionExtensions.cs`：`AddSystemCollectorInProcessBinding`。
- `Observations/`：平台无关观察模型；Windows/macOS 只实现 adapter。
- `Collection/AppMonitorService.cs`：前台、away 与标题转场状态机。
- `Collection/SystemCollectorProtocolAdapter.cs`：回调入队与后台交付边界。
- `Collection/SystemInProcessCollector.cs`：InProcess 协议参与者。
- `Input/`、`Package/`：输入事件 seam 与 Package staging 来源。

## 验证与归属

```bash
dotnet test collection/desktop/Heartbeat.Collector.System.Tests
```

Package 构建到 `CollectorPackages/System`，随 Windows/macOS Desktop release 交付。领域语义见
[Collection Context](../../CONTEXT.md)，Fact payload 见 [Contracts](../../contracts/README.md)。
