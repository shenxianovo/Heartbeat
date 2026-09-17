# Heartbeat macOS Desktop Collector

Collector 通过 macOS 原生通知和周期确认产生五条 Track：

| Track | 时间定义 | 内容 |
| --- | --- | --- |
| [`desktop.application.foreground` v1](../../../docs/protocols/desktop-application-foreground-v1.md) | `range + explicit` | 前台应用 |
| [`desktop.window.foreground` v1](../../../docs/protocols/desktop-window-foreground-v1.md) | `range + explicit` | 前台窗口标题 |
| [`desktop.system.away` v1](../../../docs/protocols/desktop-system-away-v1.md) | `range + explicit` | 明确系统离开信号 |
| [`desktop.input.event` v1](../../../docs/protocols/desktop-input-event-v1.md) | `point` | 非文本物理输入 |
| [`desktop.observation.status` v1](../../../docs/protocols/desktop-observation-status-v1.md) | `range + explicit` | 观察能力状态 |

协议语义以上述文档为准。实现使用单消费者串行投影原生事件与周期快照；连续时间按启动时 UTC 加上包含休眠的单调经过时间计算。默认最大确认间隔为采样间隔三倍，窗口标题静置时间为 1.5 秒。

## 接入与运行

先运行 [Hub](../../../docs/hub-record-delivery.md)，再启动 Collector：

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --hub http://127.0.0.1:4318 \
  --hub-token "$HEARTBEAT_HUB_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET" \
  --display-name "My Mac"
```

可用配置：

| 参数 | 环境变量 | 默认值 |
| --- | --- | --- |
| `--hub` | `HEARTBEAT_HUB_URL` | 必填 |
| `--hub-token` | `HEARTBEAT_HUB_TOKEN` | 必填 |
| `--target` | `HEARTBEAT_COLLECTOR_TARGET` | 必填 |
| `--display-name` | `HEARTBEAT_COLLECTOR_DISPLAY_NAME` | Target |
| `--interval-seconds` | `HEARTBEAT_COLLECTOR_INTERVAL_SECONDS` | 5 |
| `--maximum-gap-seconds` | `HEARTBEAT_COLLECTOR_MAXIMUM_GAP_SECONDS` | interval × 3 |
| `--window-title-dwell-ms` | `HEARTBEAT_COLLECTOR_WINDOW_TITLE_DWELL_MS` | 1500 |
| `--once` | `HEARTBEAT_COLLECTOR_ONCE` | false |

最大确认间隔必须大于采样间隔。窗口标题静置时长必须小于最大确认间隔，否则候选会先被中断清掉、永远等不到转正；设为 0 表示每次标题变化都承认。`--once` 只采集当前快照，不验证通知、Away Signal 或输入事件。

## 权限

前台应用与 Away Signal 不要求额外授权。窗口标题需要 Accessibility 权限，输入事件需要 Input Monitoring 权限。窗口标题能力失败只降级 `desktop.window.foreground`，前台应用照常记录。Collector 不主动弹出授权请求；在系统设置授予权限后，周期能力刷新会自动开始相应观察。

窗口标题能力只有在观察器附着且属性读取成功后才报告恢复。AX 不支持属性或当前没有值可返回空标题，其他读取错误报告能力失败。

## Hub 交接

五条 Track 共用一个内存待交接缓冲区；按 Hub 的 500 条和 1 MiB 限制分批。同一 Record 的新快照覆盖未交接旧快照，但旧请求回执只确认它实际发送的快照。慢请求不阻塞采样，失败或不明回执保留原 ID 重试。

Collector 不提供 Hub 之前的持久队列；进程退出会丢失尚未被 Hub 接管的内存数据。Hub 接管后的数据由其本地 SQLite outbox 保护。

## 标题探针

同一个可执行文件带一个只观察的模式，用来量前台窗口标题在真实使用中变化得多快：

```bash
dotnet bin/Debug/net10.0/Heartbeat.Collector.Desktop.Mac.dll \
  --probe-window-titles --duration-seconds 120 --output /tmp/readings.json
```

探针不需要 Hub 地址与凭据，也不产生 Record：它把原生通知与轮询读数按时间原样写进一个 JSON 文件。默认只写标题长度、指纹与相邻标题的形态度量，`--include-titles` 才写标题原文。日常从 `./scripts/heartbeat-dev probe window-title` 使用它，统计与静置参数模拟由那条命令给出，见[工程验证](../../../docs/verification.md)。

## 静置阈值复核

默认值的依据见 [ADR-0008](../../../docs/adr/ADR-0008-window-title-must-hold-still.md)。积累真实读数后运行：

```bash
./scripts/heartbeat-dev probe window-title --from-database \
  --since '<起>' --until '<止>' --dwell-seconds 1,1.5,2
```

比较循环噪声和真实标题的停留时长、各档 Record 数量及标题陈旧占比。还需要一次有 Accessibility 权限的原生复核；数据库读数无法判断通知是否漏送。

## 手工验证

启动常驻 Collector 后依次验证：切换两个应用；在同一应用切换窗口和标题；锁屏再解锁；允许 Accessibility 与 Input Monitoring 后按键、单击和双向滚动。Web 回放应在一个时间轴显示相应 Track。锁屏和休眠会改变系统状态，不应由自动测试擅自触发。

自动验证入口见[工程验证](../../../docs/verification.md)，已完成与待完成的真机范围见[系统验收](../../../docs/validation/system-acceptance.md)。
