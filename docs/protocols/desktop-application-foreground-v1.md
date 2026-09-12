# 桌面前台应用协议 v1

状态：协议定义、通用 Track 获取、批量 Record 上传接口及 Track 级重放查询已实现；最小 macOS Collector 已接入，断采规则仍待确认。

## 含义

记录 Collector 已确认的一段时间内，指定设备的系统前台应用是什么。不表达窗口、浏览器页面或人的注意力，也不判断人在阅读、工作或娱乐。

## Track

| 字段 | 固定值 |
| --- | --- |
| `type` | `desktop.application.foreground` |
| `version` | `1` |
| `time_mode` | `range` |
| `end_mode` | `explicit` |
| 允许持续状态续期 | 是 |

同一个 Collector 的此协议只有一条 Track。不同 Collector 可以使用相同协议，解码不依赖 Collector key。Collector 向 Hub 提交上表中的时间定义，由 Hub 获取后端 Track。

## Record value

```json
{
  "device_id": "device-a",
  "application": {
    "platform": "macos",
    "id_kind": "bundle_id",
    "id": "com.google.Chrome"
  }
}
```

- `value` 必须是对象，且恰好包含 `device_id` 与 `application`。
- `device_id` 是 Collector 提供的稳定设备标识，为非空字符串。它不要求是操作系统安装标识，也不引用设备表；标识的共享与恢复留到 Collector 实现时处理。当前最小 macOS Collector 暂时使用 Target 作为 `device_id`，不表示 Device Identity 注册表已经实现。
- `application` 必须是对象，且恰好包含 `platform`、`id_kind` 与 `id`，三个字段均为非空字符串。
- `platform` 与 `id_kind` 说明平台及原生标识种类，示例为 `macos` 与 `bundle_id`。协议要求上述结构和非空值，但不要求标识已在真实设备或应用登记表中存在。
- Collector 负责规范化平台及标识种类；原生 `id` 按平台规则提供。Heartbeat 后端把 `value` 当作任意 JSON 原样存储，不按本协议校验、改写、统一大小写或替换为跨平台 Application Identity；生产者和消费者负责遵守并解释本协议。
- 字段名区分大小写。符合本协议的生产者不发送缺失、未知字段、空白字符串或错误 JSON 类型。JSON 对象字段顺序不影响观测值相等判断。
- 本版本不包含应用显示名、窗口标识或标题、页面 URL、进程 ID 和通用 metadata。

## 区间

首次确认时创建 Record，可以从 `started_at = ended_at` 的零长度区间开始。相同应用且持续观测时，使用原 Record ID 续期；结束时间只增长。设备或应用观测值变化时创建新 Record。

无法取得可靠的前台应用信息时，停止延长原 Record，不使用 `application: null` 表示未知，也不凭计时器推断状态持续。恢复后无法确认连续性时，即使应用相同，也创建新 Record。该协议不以“没有采到”断言系统“没有前台应用”。

Track 不强制区间互斥，也不把新 Record 自动解释为旧 Record 的结束。协议负责记录观测，重放按已确认区间显示。

## 实现

- [`IRecordStore`](../../src/Backend/Heartbeat.Application/Recording/IRecordStore.cs) — 按 Track 公共时间定义执行幂等写入与 explicit 区间续期。
- [`ADR-0002`](../adr/ADR-0002-monotonic-record-extension.md) — 已确认区间续期规则。
- [记录 HTTP 接口](../recording-api.md) — 上传格式、逐条确认、重试和 Track 级读取。
