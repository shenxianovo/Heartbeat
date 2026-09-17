# Heartbeat macOS Desktop Collector

Collector 通过 macOS 原生通知和周期确认记录五类事实：

| Track | 时间定义 | 内容 |
| --- | --- | --- |
| `desktop.application.foreground` v1 | `range + explicit` | 前台应用与显示名 |
| `desktop.window.foreground` v1 | `range + explicit` | 前台窗口标题 |
| `desktop.system.away` v1 | `range + explicit` | 锁屏、会话失活、显示器休眠与系统休眠 |
| `desktop.input.event` v1 | `point` | 物理按键按下、鼠标按钮按下和原生滚动增量 |
| `desktop.observation.status` v1 | `range + explicit` | 各观察能力当时的可用状态 |

输入记录不保存字符或文本。按键使用物理位置码，过滤按住产生的重复 key-down，key-up 只用于解除按住状态而不写 Record；滚动保留原生正负、量级和 line/point 单位，不设置活动阈值。

Shift、Control、Option 和 Command 的左右键通过原生 `flagsChanged` 事件及各自物理状态位识别。CapsLock 仅在系统提供 stateless 物理状态位时记录按下，不把开关锁定状态当作物理按下；当前物理位置表未定义 Fn。

前台应用与前台窗口是两个观测对象，各自成 Track。同一个应用下换窗口、改标题只切窗口 Record，应用 Record 继续延长；窗口标题读不到只断开窗口 Record，不影响应用区间。这样同一个应用不会被切成多条首尾相接的 Record，展示端也不需要合并相邻块。

锁屏、会话失活、显示器休眠和系统休眠是互相独立、可以重叠的 Away Signal。任一原因仍存在时应用活动保持断开；全部恢复后创建新的应用 Record。权限缺失或原生观察器故障只降级相应能力，其他 Track 继续采集，并写入观察状态；权限恢复后无需重启进程。

读数相同且确认间隔未超过 `maximumConfirmationGap` 时复用 Record ID 并延长 `endedAt`。原生通知只是触发重新读数，读到的应用身份没变就不切分应用区间；应用身份不含进程 ID，重启同一个应用不改变身份。明确的 Away Signal、对应能力失败或超过确认间隔会打断连续性；恢复后即使读数相同也创建新 Record。默认最大确认间隔是采样间隔的三倍——一次晚到的 tick 属于正常调度抖动，连续两次缺失确认才判定观察中断，这只是当前实现规则。

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
| `--maximum-gap-seconds` | `HEARTBEAT_COLLECTOR_MAXIMUM_GAP_SECONDS` | interval × 3 |
| `--window-title-dwell-ms` | `HEARTBEAT_COLLECTOR_WINDOW_TITLE_DWELL_MS` | 1500 |
| `--once` | `HEARTBEAT_COLLECTOR_ONCE` | false |

最大确认间隔必须大于采样间隔。窗口标题静置时长必须小于最大确认间隔，否则候选会先被中断清掉、永远等不到转正；设为 0 表示每次标题变化都承认。`--once` 只采集当前快照，不验证通知、Away Signal 或输入事件。

## 权限

前台应用与 Away Signal 不要求额外授权。窗口标题需要 Accessibility 权限，输入事件需要 Input Monitoring 权限。窗口标题能力失败只降级 `desktop.window.foreground`，前台应用照常记录。Collector 不主动弹出授权请求；在系统设置授予权限后，周期能力刷新会自动开始相应观察。

窗口标题能力恢复要求观察器实际附着且属性读取成功。AX 的“不支持该属性”和“当前没有值”允许返回空标题，其他读取错误报告观察失败；发起订阅本身不代表恢复成功。订阅刚发起、握手还没完成时既不声称可用，也不宣布中断——切换应用时的这段空窗不是能力故障。

## Hub 交接

五条 Track 共用一个内存待交接缓冲区；按 Hub 的 500 条和 1 MiB 限制分批。同一 Record 的新快照覆盖未交接旧快照，但旧请求回执只确认它实际发送的快照。慢请求不阻塞采样，失败或不明回执保留原 ID 重试。

缓冲区只在锁内复制待交接快照，分组和按字节计量在锁外完成；每条记录只计量一次，避免高频输入使锁内序列化成本反复增长。

Collector 不提供 Hub 之前的持久队列；进程退出会丢失尚未被 Hub 接管的内存数据。Hub 接管后的数据由其本地 SQLite outbox 保护。

## 标题探针

同一个可执行文件带一个只观察的模式，用来量前台窗口标题在真实使用中变化得多快：

```bash
dotnet bin/Debug/net10.0/Heartbeat.Collector.Desktop.Mac.dll \
  --probe-window-titles --duration-seconds 120 --output /tmp/readings.json
```

探针不需要 Hub 地址与凭据，也不产生 Record：它把原生通知与轮询读数按时间原样写进一个 JSON 文件。默认只写标题长度、指纹与相邻标题的形态度量，`--include-titles` 才写标题原文。日常从 `./scripts/heartbeat-dev probe window-title` 使用它，统计与静置参数模拟由那条命令给出，见[工程验证](../../../docs/verification.md)。

## 静置阈值的复核

1.5 秒这个默认值是拿实现之前的历史读数定的，那份数据答不了「原生通知有没有漏送」（推导过程见 [ADR-0008](../../../docs/adr/ADR-0008-window-title-must-hold-still.md)）。攒够几天真实运行之后应该复核一次：

```bash
./scripts/heartbeat-dev probe window-title --from-database \
  --since '<起>' --until '<止>' --dwell-seconds 1,1.5,2
```

看四件事：

- spinner 这类循环噪声的停留时长上限有没有超过 1.5 秒。超过就压不住，要放宽。
- 真实标题变更的停留时长分布有没有下探到 1.5 秒附近。下探了就说明会误吸真实变更，要收紧。
- 按 1 / 1.5 / 2 秒三档模拟出来的 Record 条数与标题陈旧占比，拐点在哪一档。
- 抖动是不是仍然集中在少数几个应用上。如果散开了，说明这是普遍形态而不是个别应用的毛病，规则本身要重新想。

**还欠一次原生复核**：探针拿到 Accessibility 权限后跑一次，才能回答通知与轮询各自贡献了多少读数、有没有漏送。数据库口径的读数是投影之后的结果，分不清这两者。

## 手工验证

启动常驻 Collector 后依次验证：切换两个应用；在同一应用切换窗口和标题；锁屏再解锁；允许 Accessibility 与 Input Monitoring 后按键、单击和双向滚动。Web 回放应在一个时间轴显示相应 Track。锁屏和休眠会改变系统状态，不应由自动测试擅自触发。

原生映射、权限降级恢复、重叠 Away Signal、输入重复过滤、共享缓冲和慢交接均有自动测试；`MacRunLoopTests` 验证等待异步工作时主线程仍处理 CoreFoundation 事件。

本轮验收结果与尚未完成的真机步骤见 [system 验收记录](../../../docs/validation/system-acceptance.md)。
