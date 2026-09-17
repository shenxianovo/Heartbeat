# 桌面前台窗口协议 v1

状态：已实现。

## 含义

记录 Collector 已确认的一段时间内，指定设备的前台窗口标题是什么。它只回答「前台窗口叫什么」，不回答「前台是哪个应用」——那是 [`desktop.application.foreground` v1](desktop-application-foreground-v1.md)。两者是两个观测对象：读窗口标题要额外的辅助功能权限，会独立失败，因此单独成 Track，标题读不到时不牵连应用区间。

## Track

| 字段 | 固定值 |
| --- | --- |
| `type` | `desktop.window.foreground` |
| `version` | `1` |
| `time_mode` | `range` |
| `end_mode` | `explicit` |
| 允许持续状态续期 | 是 |

同一个 Collector 的此协议只有一条 Track。不同 Collector 可以使用相同协议，解码不依赖 Collector key。

## Record value

```json
{
  "device_id": "device-a",
  "window": {
    "title": "Heartbeat — Google Chrome"
  }
}
```

- `value` 是包含 `device_id` 与 `window` 的对象。
- `device_id` 与前台应用协议同义，是 Collector 提供的稳定设备标识，为非空字符串。
- `window.title` 是非空字符串，取平台报告的前台窗口标题原文，Collector 不改写、不截断、不脱敏。
- 没有 Record 表示该段时间没有可靠标题，不表示没有窗口，也不表示标题为空。协议不提供 `title: null`。
- 本版本不包含窗口标识、窗口几何、页面 URL、文档路径或进程 ID。
- 字段名区分大小写。JSON 对象字段顺序不影响观测值相等判断。

## 区间

标题保持相同且持续确认时使用原 Record ID 续期；结束时间只增长。前台应用切换时即使新应用的标题字符串与旧的相同，也结束旧 Record 并新开一条——那是另一个窗口，不是同一段观测。

标题变化要先站稳一段时间（默认 1.5 秒）才承认，滚动字幕、终端 spinner 与浏览器导航中间态不会各自成为 Record：

- **区间从标题第一次出现算起**，不是从承认那一刻算起。静置只推迟写入，不改区间。
- **没站稳的标题被前一个区间吸收**，不产生 Record，也不留空洞——所以一段连续的前台时间里不会因为标题抖动出现缝隙。
- **应用切换那一刻的标题立即承认**，不等静置；标题读不到、Away Signal 或能力失败发生时，当前候选若已站够时间仍会补记成 Record，不会连同中断一起丢掉。

承认发生在候选之后的第一条读数上，所以最新的标题最晚要等一个采样间隔才会写出来。静置时长可配，设为 0 表示每次标题变化都承认（仍然是下一条读数才结算）。

无法取得可靠标题、出现 Away Signal、`window_title` 或 `application` 观察能力失败、确认间隔超过 Collector 上限时停止延长原 Record。恢复后即使标题相同也创建新 Record。

标题里可能出现文件名、聊天对象、网页标题这类敏感内容。协议原样保存，隐私取舍留给读侧的可见性与过滤，不在采集端做静默丢弃。

## 相关文档

- [`desktop.observation.status` v1](desktop-observation-status-v1.md) — `window_title` 能力的可用性与失败原因。
- [`ADR-0002`](../adr/ADR-0002-monotonic-record-extension.md) — 已确认区间续期规则。
- [`ADR-0008`](../adr/ADR-0008-window-title-must-hold-still.md) — 标题静置规则和默认值依据。
