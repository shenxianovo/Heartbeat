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
| `end_mode` | `explicit` |
| 允许持续状态续期 | 是 |

同一个 Collector 的此协议只有一条 Track。解码只依赖 `(type, version)`。

## Record value

```json
{
  "device_id": "device-a",
  "application": {
    "platform": "macos",
    "id_kind": "bundle_id",
    "id": "com.google.Chrome",
    "display_name": "Google Chrome"
  }
}
```

- `value` 是包含 `device_id` 与 `application` 的对象，没有窗口字段。
- `device_id` 是 Collector 提供的非空稳定设备标识，不要求是操作系统安装标识，也不引用设备表。当前 macOS Collector 暂用 Target，不表示 Device Identity 已实现。
- `application` 包含非空的 `platform`、`id_kind` 与 `id`；可选 `display_name` 是平台提供的展示名。
- `platform` 与 `id_kind` 说明平台及原生标识种类，示例为 `macos` 与 `bundle_id`。协议要求上述结构和非空值，但不要求标识已在真实设备或应用登记表中存在。
- Collector 负责规范化平台及标识种类；原生 `id` 按平台规则提供。Heartbeat 后端把 `value` 当作任意 JSON 原样存储，不按本协议校验、改写、统一大小写或替换为跨平台 Application Identity；生产者和消费者负责遵守并解释本协议。
- 字段名区分大小写。符合本协议的生产者不发送缺失、未知字段、空白字符串或错误 JSON 类型。JSON 对象字段顺序不影响观测值相等判断。
- 本版本不包含窗口标题、窗口标识、页面 URL、进程 ID 或通用 metadata。进程 ID 是同一个应用的运行时细节，不参与应用身份，重启应用不改变身份。

## 区间

首次确认时创建 Record，可以从 `started_at = ended_at` 的零长度区间开始。应用身份保持相同且持续确认时使用原 Record ID 续期；结束时间只增长。原生通知本身不切分区间：应用激活、焦点窗口变化、标题变化只是重新读数，读到的应用身份没变就仍是同一条 Record。因此同一个应用不会出现两条首尾相接的 Record，展示端也不需要合并相邻块。

无法取得可靠信息、出现 Away Signal、`application` 观察能力失败或确认间隔超过 Collector 上限时停止延长原 Record，不使用 `application: null` 表示未知，也不凭计时器推断状态持续。恢复后即使应用相同也创建新 Record。`window_title` 能力失败只影响窗口协议，不断开本协议的区间。当前 macOS Collector 的确认上限默认是采样间隔三倍，可配置；这是实现规则而非协议不变量。

Track 不强制区间互斥，也不把新 Record 自动解释为旧 Record 的结束。协议负责记录观测，重放按已确认区间显示。

## 相关文档

- [`ADR-0002`](../adr/ADR-0002-monotonic-record-extension.md) — 已确认区间续期规则。
- [记录 HTTP 接口](../recording-api.md) — 上传格式、逐条确认、重试和 Track 级读取。
