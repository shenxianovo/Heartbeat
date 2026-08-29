# 02 — 周报贯通：本地周一到下周一

**What to build:** 当用户查看所选日期所在周时，Browser 与 Analytics 都把它解释为当地周一首个有效 instant 到下一周一首个有效 instant，而不是固定七天。owner 与 public Weekly Report 因此能在跨时区切换的周展示完整且一致的事实汇总。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** ready-for-agent

- [ ] Calendar Context 同时提供包含所选日期的 Monday-based week window，并复用 01 建立的时区快照与严格日期语义
- [ ] Analytics 对 week envelope 独立重算、精确比较并把验证后的 Instant Window 传给周报查询
- [ ] owner 与 public Weekly Report 原子改用版本化 Local Calendar Window 契约，visibility 只影响路由与授权、不影响窗口
- [ ] 周报半开裁剪覆盖普通 168 小时周、spring-forward 167 小时周与 fall-back 169 小时周
- [ ] golden scenarios 覆盖跨两种 DST transition 的周、Sunday-to-Monday 归属以及 all-device / single-device 一致性
- [ ] Dashboard 周视图可独立验证使用当地周一边界，没有任何固定 `7 × 24h` 推导
