# 桌面观察状态协议 v1

状态：已实现。

`desktop.observation.status` v1 使用 `range`，保存某项采集能力在一段时间内的已知状态。

```json
{
  "capability": "window_title",
  "state": "permission_required",
  "reason": "accessibility"
}
```

`capability` 为 `application`、`window_title` 或 `input`；`state` 为 `available`、`permission_required` 或 `unavailable`。`reason` 可选，用稳定的机器可读字符串说明限制来源。

`available` 只表示观察能力在该区间可工作，不承诺没有事件丢失，也不证明数据完整。状态变化创建新 Record；权限恢复后 Collector 可以建立新的 available 区间并恢复相应 Track，无需重启。

设备由公共 `objects` 的 `device` 引用承载，namespace 为 `device`，key 为配置 Target；不在 value 重复设备身份。见[对象契约](../record-objects.md)。
