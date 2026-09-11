# System Collector

平台无关的内置 system Collector。它消费语义化桌面观察，产出 foreground Segment 与
Input Event，通过公开 Collector SDK 和 InProcess Collector Protocol 汇入 Runtime，
由 `/observations` 保存独立事实。Package 1.2.0 声明 `facts.observation:2`。

## 目录

- `Collection/SystemCollectorServiceCollectionExtensions.cs`：`AddSystemCollectorInProcessBinding`。
- `Observations/`：平台无关观察模型；Windows/macOS 只实现 adapter。
- `Observations/SystemActivityModel.cs`：前台、away 与标题的业务转场规则，不管理 Fact 身份或交付。
- `Collection/AppMonitorService.cs`：把活动转场转换为独立 Segment，管理修订、定时快照和交付顺序。
- `Collection/SystemCollectorProtocolAdapter.cs`：回调入队与后台交付边界。
- `Collection/SystemInProcessCollector.cs`：InProcess 协议参与者。
- `Input/`、`Package/`：输入事件 seam 与 Package staging 来源。

## 验证与归属

一个活动模型实例观察当前绑定的本机桌面；应用和标题是活动读数，窗口切换是转场证据。
不额外登记窗口或创建持久对象。模型接受的当前活动与最近采样的标题分开：未通过点击门控的
标题变化不会替换活动起始时的标题，也不会切段。定时快照延续当前 Fact，业务转场结束旧活动；
现有超长 Segment 轮转仍由 Fact 输出层处理。

链路：平台回调 → `DesktopObservation` → `SystemActivityModel` → `AppMonitorService` →
`SystemCollectorProtocolAdapter` → `CollectorProtocolClient` → InProcess Runtime → HTTP → PostgreSQL。
InputEventBuffer 对平台物理键、按钮与滚轮产生 UUIDv7 Event，随后走相同 SDK 和持久交付。
新事实的 Kind、Source、Collector、FOI、Aspect 和完整 Result 由生产者提供；空 BindingId 的原生发布
不依赖 Stream。仍打开的 foreground / input-events Stream 只承接历史 Fact 与真实 Gap。
CollectorId 使用持久 System Instance UUID，FOI 使用实际 Machine；两者跨重启稳定。
App 关系仅来自实际平台身份；无 App 的桌面、away 仍有完整机器活动 Result，且不造 App 关系。
同活动的增长、缩短与收尾沿原 Id 递增 Revision；转场与轮转新建 Id。

```bash
dotnet test collection/desktop/Heartbeat.Collector.System.Tests
```

测试以可控的平台观察和时间覆盖活动规则、快照及协议输出。真机核对时，使用包含本次改动的
Desktop 构建切换应用/窗口、停留超过 30 秒、离开后恢复，确认活动切换与连续时长。
自动测试通过不代表该构建已安装，也不代表已完成原生权限及回调的真机验收。

Package 构建到 `CollectorPackages/System`，随 Windows/macOS Desktop release 交付。领域语义见
[Collection Context](../../CONTEXT.md)，Fact payload 见 [Contracts](../../contracts/README.md)。


## 持久状态与回退

当前 .NET System 没有单独的 Segment SDK 状态文件：AppMonitorService 管理活动 Id/Revision，
`system-collector-ingress.json`（及 `.NNNNNNNN.chunk`）保存 first-stage 队列和 active checkpoint，
通用 CollectorProtocolClient 管理 `collector-protocol-outbox.json`。Browser 的 TypeScript Segment SDK
不在此依赖链上。Runtime/SDK 的通用格式与退出要求见
[缓存兼容交接](../../../docs/architecture/observation-cache-compatibility.md)。

- 旧 NDJSON 条目无版本（schema 1），未携带 IsObservation 的活动和 input 继续旧 Kind=null 身份；
  原 Binding/Stream/FactId/Revision 保持到正常收尾和准确 ACK，不将旧客户端 Id 当作数据库行 Id。
- 新 ingress mutation 写 schema 2，原生活动/input 明确 `IsObservation=true`；包括空 reset 的所有
  后续写入都保留版本栅栏。升级不改旧行，原子替换前失败保留原字节，重试不重新分配身份。
- 重启时 checkpoint 以原 End、原 Id、Revision+1 收尾；停机区间只生成 `process_restart` Gap，
  不延长没有观察依据的活动。Runtime 已确认但尚未终结的事实仍占有保管责任。
- 旧 `segments` / `input-events` JSON 仍由 HeartbeatCacheFormats 和原上传源排空；旧 AppName /
  windows-vk-v1 原编码、身份与备份继续保留，新事实不写回该缓冲。
- `python3 scripts/verify-system-ingress-rollback.py` 编译固定基线 e6564fc 的实际旧 loader，验证新输入、
  已 ACK checkpoint 和排空 reset 均被拒绝；文件集合及 SHA256 不变，再用新版恢复。它需要 Git、
  Python3 和 .NET 10，仅使用临时目录。该版本保护不能通过自动恢复旧备份绕过。

兼容消费者是仍持有这些旧队列、进行中 checkpoint、SDK outbox 或隔离记录的 Desktop Profile。
移除兼容前，owner 必须提供所有实际安装 Package/version/contentHash 与数据目录 schema、未发/未终结/
隔离数量、最老待发时间，并确认最长离线和回退窗口。所有记录有归宿且跨过批准窗口才允许移除。
当前现场清单和时间窗口尚未提供；自动 fixture 不是现场安装证据。

## 平台验证与人工承接

| 路径 | 生产依赖 | 自动验证边界 |
| --- | --- | --- |
| Windows | WindowsDesktopObservationSource（WinEvent）、InputEventCollector / LowLevelInputHook、InputEventBuffer | Windows adapter 测试、共用 System 活动 fixture 以 win: 身份进入真实 SDK/Runtime/HTTP/PG |
| macOS | MacDesktopObservationSource（Workspace/Accessibility）、MacInputEventCollector / MacInputNativeEventTranslator、InputEventBuffer | macOS adapter 测试、共用 System 活动 fixture 以 mac: 身份进入真实 SDK/Runtime/HTTP/PG |
| 两平台持久保管 | BuiltIn System Package → AppMonitor/ingress → SDK schema4 → Runtime schema9 | 真正 subprocess crash、两次重启、旧 loader、晚 ACK、混合上传失败和 Gap 重试 |

原生权限、真实窗口/输入回调、实际安装制品还需 owner 在 Windows 和 macOS 分别验收。本票保持
`ready-for-human`，不能把一个平台的构建或可控 observation fixture 视为两平台真机验收。

1. 使用独立 Desktop Profile 和隔离 Analytics/PostgreSQL，记录 OS/架构、Desktop 版本、System
   Package 1.2.0 的实际 contentHash、Profile 路径、Runtime/SDK/ingress 格式及旧缓存盘点。
   不连接业务库；账号与安装操作由 owner 执行。
2. macOS 分别检查 Accessibility/Input Monitoring 授权与拒绝后恢复；Windows 检查 WinEvent/低层
   hook 正常及安全桌面/恢复。确认 Interaction Signal 与 InputEvent Recording 仍为独立设置。
3. 停留应用超过 30 秒、修改标题（含无点击与有点击）、换应用、进入 away、恢复至无 App 桌面，再
   返回应用。读回机器 FOI、明确 App 关系；无 App/away 不出现虚构 App，原活动增长/收尾 Id 不变。
4. 开启 InputEvent Recording 后按下/长按/抬起物理键、鼠标按钮、细粒度滚轮。确认自动重复过滤、
   滚轮累计、完整 CodeSet/Code、时刻与 UUID；离线重传后仍唯一。
5. 阻断隔离上传并保留开放活动与输入，停止/崩溃后重启。核对旧事实以原 End 收尾，停机仅有 Gap；
   恢复网络后新旧批次及 Gap 排空，迟到 ACK 不清除新修订。保存相应查询和本地未发数量证据。
6. 在 Profile 副本验证旧包拒绝新 journal 且内容不变，再用新版恢复。记录最长离线/回退窗口；
   不用旧备份覆盖新状态，不把模拟故障时长冒充实际支持窗口。

全库历史副本的数据 smoke、生产迁移/资源与发布仍由 observation-storage 发布门禁及协调任务承接；
本票自动证据使用可重复的隔离 PostgreSQL HTTP fixture，不代表已恢复暂停的完整生产副本演练。
