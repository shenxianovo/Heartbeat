# 桌面系统离开信号协议 v1

状态：已实现。

`desktop.system.away` v1 使用 `range + explicit`，记录设备上一个明确系统状态信号持续的已确认区间。它不证明人实际离开，也不把无输入或观察空白解释为离开。

```json
{
  "device_id": "device-a",
  "reason": "screen_locked"
}
```

`device_id` 和 `reason` 均为非空字符串。v1 的原因是 `screen_locked`、`session_inactive`、`display_sleep` 或 `system_sleep`。不同原因是独立 Record，可以重叠；某原因的恢复只结束对应 Record。任何原因存在时前台应用连续性断开，全部原因恢复后应用以新 Record 重新开始。

后端原样保存 value，不判断人在场，也不要求 Away 区间互斥。
