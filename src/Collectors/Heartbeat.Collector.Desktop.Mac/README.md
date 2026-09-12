# Heartbeat macOS Desktop Collector

最小 macOS Collector 会注册 `heartbeat.collector.desktop.macos`，获取
`desktop.application.foreground` v1 Track，并轮询系统前台应用上传已确认区间。

当前 `--target` 同时作为 Collector Target 和协议载荷中的 `device_id`。这只是最小实现的传递方式，不表示设备表或 Device Identity 注册表已经实现；设备关联、断采规则和持久上传队列见[记录模型未决设计](../../../../docs/recording-open-questions.md)。

## 运行

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --api http://localhost:8080 \
  --token "$HEARTBEAT_AUTH_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET" \
  --display-name "My Mac"
```

一次性冒烟：

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --api http://localhost:8080 \
  --token "$HEARTBEAT_AUTH_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET" \
  --once
```

也可以使用环境变量：

- `HEARTBEAT_API_BASE_URL`
- `HEARTBEAT_AUTH_TOKEN`
- `HEARTBEAT_COLLECTOR_TARGET`
- `HEARTBEAT_COLLECTOR_DISPLAY_NAME`
- `HEARTBEAT_COLLECTOR_INTERVAL_SECONDS`
- `HEARTBEAT_COLLECTOR_ONCE`

采样和上传独立运行。常驻运行时，上传失败会输出错误，保留内存中的待上传记录并重试；同一 Record 合并最新进度。启动准备失败或 `--once` 上传失败时以非零退出码退出；用户按 Ctrl+C 取消时正常退出。

本阶段没有本地持久上传队列，退出可能丢失未确认记录；长时间离线期间，不同 Record 的积压可能使内存用量增长。休眠和采样中断规则仍待确认。
