# Heartbeat macOS Desktop Collector

当前只采集 **macOS 前台应用**，默认每 5 秒采样一次。没有采集键鼠事件、窗口标题、URL 或页面内容。

Collector 负责调用平台接口、判断观测连续性、形成 Record，并把完整记录交给 Hub。后端注册、Track 解析、持久队列和上传由 Hub 负责。

## 采集内容

数据类型是 `desktop.application.foreground` v1，时间形状为 `range + explicit`。记录中的 value 示例：

```json
{
  "device_id": "device-a",
  "application": {
    "platform": "macos",
    "id_kind": "bundle_id",
    "id": "com.apple.finder"
  }
}
```

当前 Target 同时用作 device_id。应用标识来自 macOS，应用显示名称和跨平台应用身份不写入该值。具体含义见[前台应用数据定义](../../../docs/protocols/desktop-application-foreground-v1.md)；后端不注册或校验这些具体字段。

首次确认创建 UUID v7 Record；应用保持相同并继续观测时，保持 ID 和 value，延长 endedAt。应用变化或读取失败后恢复时创建新 Record。休眠与采样调度空白的识别仍待讨论。

## 接入与运行

先运行 [Hub](../../../docs/hub-record-delivery.md)，再启动 Collector：

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --hub http://127.0.0.1:4318 \
  --hub-token "$HEARTBEAT_HUB_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET" \
  --display-name "My Mac"
```

不需要后端 JWT、Collector ID 或 Track ID。Collector 提交自身的 key/target/displayName、Track 的 type/version/timeMode/endMode 和 Record。Hub 在本地持久接管后，自行完成后端注册与上传，首次接入也允许后端离线。

一次采集并等待 Hub 接管：

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --hub http://127.0.0.1:4318 \
  --hub-token "$HEARTBEAT_HUB_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET" \
  --once
```

可用配置：

| 参数 | 环境变量 |
| --- | --- |
| `--hub` | `HEARTBEAT_HUB_URL` |
| `--hub-token` | `HEARTBEAT_HUB_TOKEN` |
| `--target` | `HEARTBEAT_COLLECTOR_TARGET` |
| `--display-name` | `HEARTBEAT_COLLECTOR_DISPLAY_NAME` |
| `--interval-seconds` | `HEARTBEAT_COLLECTOR_INTERVAL_SECONDS` |
| `--once` | `HEARTBEAT_COLLECTOR_ONCE` |

displayName 默认使用 Target；采样间隔至少 1 秒。Hub 地址必须是 HTTP(S) origin。

## 交接行为

可复用的 `Heartbeat.Hub.Client` 封装 HTTP 提交和回执核对。常驻采样与向 Hub 提交独立运行，慢请求不阻塞采样。同 Record 的待交接快照合并最新进度，旧回执不能清除新进度；失败或不明回执保留原 ID 重试。

`--once` 成功表示 Hub 已持久接管，不表示后端已上传。交接失败以非零退出码退出。Collector 停止后，独立运行的 Hub 继续上传积压。

尚未交接的数据仍在 Collector 内存中；进程退出可能丢失该部分数据，长时间无法交接可能积累内存。Hub 接管后的数据由 SQLite 保护。相关限制见[未决设计](../../../docs/recording-open-questions.md)。
