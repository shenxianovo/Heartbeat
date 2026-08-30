# 03 — 活动视图贯通：一个日窗驱动全部事实视图

**What to build:** 当用户在日报与活动视图之间查看同一天时，Timeline、Usage、App Detail segments 与 Key Frequency 全部使用 Calendar Context 已解析的精确 day endpoints。DST 日的时间轴长度、应用明细和键频统计因此与日报讲述同一段事实，同时通用 Instant Window 查询仍可独立使用。

**Blocked by:** 01 — 日报主射弹：Calendar Context 到严格验证.

**Status:** done

- [x] Dashboard adapters 把同一 Calendar Context 的 day start/end-exclusive 传给 Usage、Segments 与 Key Frequency，不自行重算日期或结束时间
- [x] Timeline 的刻度、布局与数据裁剪支持 23、24、25 小时日，不假设 86,400,000 毫秒
- [x] 展开 App Detail 时复用发起刷新时的 Calendar Context，慢响应不能因重新读取当前日期而查询另一个窗口
- [x] all-device 与 single-device 切换只改变设备过滤，不改变 Local Calendar Window
- [x] Usage、Segments 与 Key Frequency 的通用 Instant Window 接口保持可独立调用，未来任意范围调用方无需理解 civil calendar
- [x] 接口级测试证明日报、Timeline、Usage、App Detail 与 Key Frequency 收到完全相同的 day endpoints，并覆盖两端半开边界
- [x] Episode 与 Strand 的 DateOnly 行为以及 zero-length Segment 差异均未被本 ticket 顺带修改

## Comments

- 2026-08-29：实现完成。Dashboard 将同一不可变 day window 传给 Daily Report、Usage、Timeline、Key Frequency 与展开时捕获的 App Detail；Timeline 的简略格、刻度、minimap、拖拽/缩放和回放裁剪均以精确 endpoints 驱动，支持 23/24/25 小时日与 fall-back 重复 civil hour。
- 自动验证：`npm test`（32 files / 228 tests）、`npm run build`、`dotnet test Heartbeat.slnx --no-restore`（867 tests）、`dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore` 全部通过。定向覆盖同一 23 小时 endpoints 贯通、all/single-device 只改过滤、App Detail 慢响应上下文、任意 Instant Window transport、Timeline 23/24/25 小时布局，以及 Usage / Segments / Key Frequency 两端半开边界；zero-length Segment 起点包含差异被保留并锁定。
- Code review：Standards 与 Spec 两轴并行完成。审查发现的 tooltip 时区撕裂、Usage 派生时长未裁剪、App Detail 标题明细未裁剪与重复裁剪逻辑均已修正；复验 0 个未解决 findings。
