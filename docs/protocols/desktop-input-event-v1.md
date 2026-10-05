# 桌面输入事件协议 v1

状态：已实现。

`desktop.input.event` v1 使用 `point`，记录不包含文本含义的物理输入事件。

按键按下：

```json
{
  "kind": "key_down",
  "code_set": "heartbeat-key-position-v1",
  "code": 4
}
```

鼠标按钮按下：

```json
{
  "kind": "mouse_button_down",
  "button": 1
}
```

滚动：

```json
{
  "kind": "scroll",
  "delta_x": 0,
  "delta_y": -17,
  "unit": "point"
}
```

按键码表达键盘物理位置，不表达字符；按住产生的重复 key-down 被过滤，key-up 不保存。鼠标按钮采用 1 起始编号。滚动保留平台报告的符号和量级，`unit` 为 `line` 或 `point`；协议不设置“活动”阈值，也不把不同单位换算为共同强度。

输入 Track 只说明观察器收到相应系统事件，不证明所有物理输入都被完整捕获。权限状态和观察器故障应结合 `desktop.observation.status` 判断。

设备由公共 `objects` 的 `device` 引用承载，namespace 为 `device`，key 为配置 Target；不在 value 重复设备身份。见[对象契约](../record-objects.md)。
