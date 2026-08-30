# 06 — 整页刷新一致性：不可变 Context 与过期响应隔离

**What to build:** 每次 Dashboard 刷新只捕获一次当前浏览器 timezone 并解析一个不可变 Calendar Context，随后 Report、Replay、Recap、Asking 与相关活动读取共享它。用户在请求进行中切换日期或旅行到新时区时，新语义从下一次刷新开始生效，旧响应无法覆盖新页面。

**Blocked by:** 02 — 周报贯通：本地周一到下周一; 03 — 活动视图贯通：一个日窗驱动全部事实视图; 04 — Recap 窗口身份：缓存、生成锁与 SSE; 05 — Asking 窗口身份：问题缓存与提案提交不漂移.

**Status:** done

- [x] 一次刷新只解析一次 Calendar Context，所有当次 day/week 请求、命令与 stream 都消费该不可变实例
- [x] Browser timezone 变化只影响下一次刷新；已经发出的请求继续保留其原 Context，直到完成或取消
- [x] selected date、timezone 或 refresh generation 已变化时，旧普通响应、App Detail 响应与 SSE event 均不能覆盖新状态
- [x] `isToday`、时区标签、live/historical UI 状态以及生成按钮行为全部来自同一 Context，不各自读取当前时钟
- [x] Report、Replay、Recap、Asking 与 Knowledge orchestration 不引入囊括全部操作的 Window Session；各模块只接收所需 Context
- [x] orchestration tests 证明单次解析、跨消费者同一 identity、下一刷新时区切换和慢旧响应隔离
- [x] 在普通日与 DST transition 日进行端到端刷新时，页面各区域展示和请求窗口完全一致

## Comments

- 2026-08-29：实现完成。首屏 setup 捕获的不可变 Calendar Context 直接供首次 Report/Replay、Recap、Asking 与 Knowledge 编排共同消费，不再在 mounted 阶段二次解析；30s today report poll 复用当前 generation，下一次显式 refresh 才重新捕获浏览器 timezone。
- TDD 证据：先观察到首屏解析两次、慢旧普通响应覆盖 app state、App Detail 不随 generation 重取、同窗口旧 SSE 未隔离、旧 Asking response 覆盖新列表，以及历史/today poll 制造隐式 generation 的失败测试，再逐片实现到 green。普通日 `Asia/Shanghai` 与 fall-back `America/New_York` 的 Dashboard orchestration render 证明 Replay、Recap、Asking、`isToday` 与时区标签共享同一 Context identity；spring-forward 窗口覆盖跨消费者 transport 与旅行时区下一刷新切换。
- 自动验证：最终 review 修复后重新执行 `npm test`（36 files / 251 tests）与 `npm run build`；`dotnet test Heartbeat.slnx --no-restore`（884 tests）、`dotnet build Heartbeat.slnx --no-restore`（0 warnings / 0 errors）、`dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`、聚焦 `vue-tsc -b` 与 `git diff --check` 全部通过。
- Code review：Standards / Spec 双轴以 `483f164` 为 fixed point 并行完成；首轮发现的命名与 App Detail identity data clump、status/admin 普通响应 race、Knowledge 在途写回、无效 Context 未作废旧 generation、E2E 请求窗口证据不足均已修正。最终复验无未解决的 Standards 或 Spec finding。
