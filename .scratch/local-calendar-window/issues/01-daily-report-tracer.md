# 01 — 日报主射弹：Calendar Context 到严格验证

**What to build:** 当用户在 Dashboard 选择一个必填本地日期时，Browser 用当前 IANA civil timezone 解析出不可变 Calendar Context，并把版本、窗口类型、本地日期、时区和完整 UTC 半开区间发送给 Analytics。Analytics 独立重算并严格比对后，让 owner 与 public Daily Report 使用同一个真实日窗；普通日、DST 23/25 小时日、午夜 gap 与整日跳过都得到明确且一致的结果。

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] Browser 通过 Temporal-compatible adapter 严格解析 `yyyy-MM-dd`，捕获当前 IANA timezone，并产出不可变 day Calendar Context、`isToday`、展示标签与仅用于关联响应的 correlation identity
- [ ] Analytics 通过 NodaTime/TZDB 从版本、kind、LocalDate 与 timezone 独立重算 UTC 起止；只有完全一致的 envelope 才能进入事实查询
- [ ] invalid local date、unsupported timezone、nonexistent civil date 与 `calendar_rules_mismatch` 都有稳定、可诊断的失败响应，验证失败时不查询事实
- [ ] owner 与 public Daily Report 原子改用版本化 Local Calendar Window 契约，不再接受可选日期或单个 fixed-offset date 作为日历接口
- [ ] Daily Report 对窗口两端执行半开裁剪，并覆盖 23、24、25 小时日以及 all-device / single-device 一致性
- [ ] Browser 与 Analytics 消费同一组语言无关 golden scenarios，覆盖普通日、spring-forward、fall-back、leap day、midnight gap 与整日跳过
- [ ] 当前日报 UI 能显示该 Calendar Context 的日期及时区含义，且 owner/public 可独立验证端到端结果
