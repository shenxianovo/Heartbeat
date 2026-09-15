# 桌面观察状态协议 v1

状态：已实现。

`desktop.observation.status` v1 使用 `range + explicit`，保存某项采集能力在一段时间内的已知状态。

```json
{
  "device_id": "device-a",
  "capability": "window_title",
  "state": "permission_required",
  "reason": "accessibility"
}
```

`capability` 为 `application`、`window_title` 或 `input`；`state` 为 `available`、`permission_required` 或 `unavailable`。`reason` 可选，用稳定的机器可读字符串说明限制来源。

`available` 只表示观察能力在该区间可工作，不承诺没有事件丢失，也不证明数据完整。状态变化创建新 Record；权限恢复后 Collector 可以建立新的 available 区间并恢复相应 Track，无需重启。
