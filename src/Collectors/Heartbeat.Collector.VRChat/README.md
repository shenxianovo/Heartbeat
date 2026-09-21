# VRChat Collector

复用 main 的 `VRChatApi.cs` API/session 适配：`VRChat.API` SDK、账号登录、email OTP/TOTP、Cookie 会话和当前位置读取。旧 `Activation/Challenge/Drain/Fact` 框架不参与当前实现；采集结果直接交给本地 Hub，遵守当前 immutable value + endedAt 单调延长的 Record 契约。

Collector key 为 `heartbeat.collector.vrchat`，Target 是规范的 VRChat `usr_<小写 UUID>`。配置包括显示名和 60–3600 秒采样间隔；用户名、密码、验证码只用于认证，加密保存成功后的会话 Cookie。重启恢复会话，认证失效显示需要认证，不自动保存或重试密码。

只从当前账号 `Presence.World`、`Presence.Instance` 及同次返回的账号 ID 建立位置观测。空值、不可用位置或请求错误表示观测空白，不生成“离线”结论。不使用 `/auth/user.state` 推断在线。连续同账号、世界、实例且间隔未超过采样周期的 2.5 倍时延长原 Record；中断、暂停、重启或超限后新建。世界名称变化不会修改既有 Record 的 value。

Track：`vrchat.location` / version `1` / `range` / `explicit`。value：

```json
{"account_id":"usr_…","world_id":"wrld_…","world_name":"世界名称或 null","instance_id":"实例标识"}
```

始末时间只表示采样所确认的区间，首个观测是零长度 Range；不外推最后观测后的在线时间。API 错误用有上限的退避重试，缺口不补成连续区间。Hub 接管失败保留最近待提交快照并重试；暂停做一次有期限的最终交接，进程崩溃前未接管的内存快照没有持久性保证。

Web 使用现有通用 Record 展示，尚无 VRChat 专用展示。测试使用模拟第三方 API 和真实 SQLite；线上账号登录、两步验证与实际位置需人工验收。API 事实及来源见 [研究记录](../../../docs/research/vrchat-collector-api.md)。
