# 02 — 周报贯通：本地周一到下周一

**What to build:** 当用户查看所选日期所在周时，Browser 与 Analytics 都把它解释为当地周一首个有效 instant 到下一周一首个有效 instant，而不是固定七天。owner 与 public Weekly Report 因此能在跨时区切换的周展示完整且一致的事实汇总。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** done

- [x] Calendar Context 同时提供包含所选日期的 Monday-based week window，并复用 01 建立的时区快照与严格日期语义
- [x] Analytics 对 week envelope 独立重算、精确比较并把验证后的 Instant Window 传给周报查询
- [x] owner 与 public Weekly Report 原子改用版本化 Local Calendar Window 契约，visibility 只影响路由与授权、不影响窗口
- [x] 周报半开裁剪覆盖普通 168 小时周、spring-forward 167 小时周与 fall-back 169 小时周
- [x] golden scenarios 覆盖跨两种 DST transition 的周、Sunday-to-Monday 归属以及 all-device / single-device 一致性
- [x] Dashboard 周视图可独立验证使用当地周一边界，没有任何固定 `7 × 24h` 推导

## Comments

- 2026-08-29：实现完成。Browser Calendar Context 同时解析 day/week envelope；Analytics 用 NodaTime/TZDB 独立验证 week envelope，owner/public Weekly Report 与 regenerated client 原子切换到版本 1 契约，旧 `DateRange.Week(date)` 路径已移除。
- 自动验证：`dotnet test Heartbeat.slnx --no-restore`（865 tests）、`npm test`（29 files / 208 tests）、`npm run build`、`dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 全部通过。定向测试覆盖共享 168/167/169 小时 golden scenarios、Sunday-to-Monday 归属、两端半开裁剪、all/single device、owner/public HTTP 与 transport 契约、缺失 envelope、精确 mismatch 和验证失败零事实查询。
- Code review：Standards 与 Spec 两轴并行完成；Spec 0 findings。Standards 的 issue closeout、API 文档漂移与重复 kind 分支均已修正。
