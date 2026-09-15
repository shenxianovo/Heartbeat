# Heartbeat macOS Desktop Collector

Collector 通过 macOS 原生通知和周期确认记录四类事实：

| Track | 时间定义 | 内容 |
| --- | --- | --- |
| `desktop.application.foreground` v1 | `range + explicit` | 前台应用、显示名与窗口标题 |
| `desktop.system.away` v1 | `range + explicit` | 锁屏、会话失活、显示器休眠与系统休眠 |
| `desktop.input.event` v1 | `point` | 物理按键按下、鼠标按钮按下和原生滚动增量 |
| `desktop.observation.status` v1 | `range + explicit` | 各观察能力当时的可用状态 |

输入记录不保存字符或文本。按键使用物理位置码，过滤按住产生的重复 key-down，key-up 只用于解除按住状态而不写 Record；滚动保留原生正负、量级和 line/point 单位，不设置活动阈值。

Shift、Control、Option 和 Command 的左右键通过原生 `flagsChanged` 事件及各自物理状态位识别。CapsLock 仅在系统提供 stateless 物理状态位时记录按下，不把开关锁定状态当作物理按下；当前物理位置表未定义 Fn。

锁屏、会话失活、显示器休眠和系统休眠是互相独立、可以重叠的 Away Signal。任一原因仍存在时应用活动保持断开；全部恢复后创建新的应用 Record。权限缺失或原生观察器故障只降级相应能力，其他 Track 继续采集，并写入观察状态；权限恢复后无需重启进程。

应用相同且确认间隔未超过 `maximumConfirmationGap` 时复用 Record ID 并延长 `endedAt`。应用、窗口或标题通知会立即产生新的 Record，不需要输入事件确认。明确的 Away Signal、观察失败或超过确认间隔会打断连续性；恢复后即使应用相同也创建新 Record。默认最大确认间隔是采样间隔的两倍，这只是当前实现规则。

原生事件在进入 Session 时记录接收时间，事件与周期快照由一个消费者串行投影。采样期间若收到状态变化，丢弃可能过期的快照，先处理事件。时间以进程启动时的 UTC 为基准，加上包含系统休眠的 macOS 单调经过时间，避免改钟或休眠使记录倒退、落后。

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
| `--maximum-gap-seconds` | `HEARTBEAT_COLLECTOR_MAXIMUM_GAP_SECONDS` | interval × 2 |
| `--once` | `HEARTBEAT_COLLECTOR_ONCE` | false |

最大确认间隔必须大于采样间隔。`--once` 只采集当前快照，不验证通知、Away Signal 或输入事件。

## 权限

前台应用与 Away Signal 不要求额外授权。窗口标题需要 Accessibility 权限，输入事件需要 Input Monitoring 权限。Collector 不主动弹出授权请求；在系统设置授予权限后，周期能力刷新会自动开始相应观察。

窗口标题能力恢复要求观察器实际附着且属性读取成功。AX 的“不支持该属性”和“当前没有值”允许返回空标题，其他读取错误报告观察失败；发起订阅本身不代表恢复成功。

## Hub 交接

四条 Track 共用一个内存待交接缓冲区；按 Hub 的 500 条和 1 MiB 限制分批。同一 Record 的新快照覆盖未交接旧快照，但旧请求回执只确认它实际发送的快照。慢请求不阻塞采样，失败或不明回执保留原 ID 重试。

缓冲区只在锁内复制待交接快照，分组和按字节计量在锁外完成；每条记录只计量一次，避免高频输入使锁内序列化成本反复增长。

Collector 不提供 Hub 之前的持久队列；进程退出会丢失尚未被 Hub 接管的内存数据。Hub 接管后的数据由其本地 SQLite outbox 保护。

## 手工验证

启动常驻 Collector 后依次验证：切换两个应用；在同一应用切换窗口和标题；锁屏再解锁；允许 Accessibility 与 Input Monitoring 后按键、单击和双向滚动。Web 回放应在一个时间轴显示相应 Track。锁屏和休眠会改变系统状态，不应由自动测试擅自触发。

原生映射、权限降级恢复、重叠 Away Signal、输入重复过滤、共享缓冲和慢交接均有自动测试；`MacRunLoopTests` 验证等待异步工作时主线程仍处理 CoreFoundation 事件。

本轮验收结果与尚未完成的真机步骤见 [system 验收记录](../../../docs/validation/system-acceptance.md)。
