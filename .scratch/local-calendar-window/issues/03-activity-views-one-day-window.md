# 03 — 活动视图贯通：一个日窗驱动全部事实视图

**What to build:** 当用户在日报与活动视图之间查看同一天时，Timeline、Usage、App Detail segments 与 Key Frequency 全部使用 Calendar Context 已解析的精确 day endpoints。DST 日的时间轴长度、应用明细和键频统计因此与日报讲述同一段事实，同时通用 Instant Window 查询仍可独立使用。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** ready-for-agent

- [ ] Dashboard adapters 把同一 Calendar Context 的 day start/end-exclusive 传给 Usage、Segments 与 Key Frequency，不自行重算日期或结束时间
- [ ] Timeline 的刻度、布局与数据裁剪支持 23、24、25 小时日，不假设 86,400,000 毫秒
- [ ] 展开 App Detail 时复用发起刷新时的 Calendar Context，慢响应不能因重新读取当前日期而查询另一个窗口
- [ ] all-device 与 single-device 切换只改变设备过滤，不改变 Local Calendar Window
- [ ] Usage、Segments 与 Key Frequency 的通用 Instant Window 接口保持可独立调用，未来任意范围调用方无需理解 civil calendar
- [ ] 接口级测试证明日报、Timeline、Usage、App Detail 与 Key Frequency 收到完全相同的 day endpoints，并覆盖两端半开边界
- [ ] Episode 与 Strand 的 DateOnly 行为以及 zero-length Segment 差异均未被本 ticket 顺带修改
