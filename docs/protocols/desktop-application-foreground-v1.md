# 桌面前台应用协议 v1

状态：已实现。

## 含义

记录 Collector 已确认的一段时间内，指定设备的系统前台应用是什么。它只回答「前台是哪个应用」，不表达窗口标题、浏览器页面或人的注意力，也不判断人在阅读、工作或娱乐。窗口标题是另一个观测对象，走 [`desktop.window.foreground` v1](desktop-window-foreground-v1.md)。

## Track

| 字段 | 固定值 |
| --- | --- |
| `type` | `desktop.application.foreground` |
| `version` | `1` |
| `time_mode` | `range` |
| 允许持续状态续期 | 是 |

同一个 Collector 的此协议只有一条 Track。解码只依赖 `(type, version)`。

## Record objects 与 value

`value` 为 `{}`。对象身份和名称由公共 `objects` 承载：

```json
[
  {"role": "device", "namespace": "device", "key": "device-a", "name": "My Mac"},
  {"role": "application", "namespace": "app.macos.bundle_id", "key": "com.google.Chrome", "name": "Google Chrome"}
]
```

设备和应用均须明确引用。原生应用 key 按平台规则提供，不把显示名或进程 ID 用作身份。macOS bundle ID 跨设备共用对象；只有 executable path 时按 `[deviceKey, path]` 的 JSON 字符串限定设备作用域。当前 device key 是配置 Target，跨重装恢复仍待实现。

后端只验证[公共对象契约](../record-objects.md)，不识别应用协议，不统一不同平台的身份。此协议没有窗口标题、页面 URL、进程 ID 或通用 metadata。

## 区间

首次确认时创建 Record，可以从 `started_at = ended_at` 的零长度区间开始。应用身份保持相同且持续确认时使用原 Record ID 续期；结束时间只增长。原生通知本身不切分区间：应用激活、焦点窗口变化、标题变化只是重新读数，读到的应用身份没变就仍是同一条 Record。名称快照变化会开始新 Record，以保留当时名称。

无法取得可靠信息、出现 Away Signal、`application` 观察能力失败或确认间隔超过 Collector 上限时停止延长原 Record，不使用 `application: null` 表示未知，也不凭计时器推断状态持续。恢复后即使应用相同也创建新 Record。`window_title` 能力失败只影响窗口协议，不断开本协议的区间。当前 macOS Collector 的确认上限默认是采样间隔三倍，可配置；这是实现规则而非协议不变量。

Track 不强制区间互斥，也不把新 Record 自动解释为旧 Record 的结束。协议负责记录观测，重放按已确认区间显示。

## 相关文档

- [`ADR-0002`](../adr/ADR-0002-monotonic-record-extension.md) — 已确认区间续期规则。
- [记录 HTTP 接口](../recording-api.md) — 上传格式、逐条确认、重试和 Track 级读取。
