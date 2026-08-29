# 06 — 整页刷新一致性：不可变 Context 与过期响应隔离

**What to build:** 每次 Dashboard 刷新只捕获一次当前浏览器 timezone 并解析一个不可变 Calendar Context，随后 Report、Replay、Recap、Asking 与相关活动读取共享它。用户在请求进行中切换日期或旅行到新时区时，新语义从下一次刷新开始生效，旧响应无法覆盖新页面。

**Blocked by:** 02 — 周报贯通：本地周一到下周一; 03 — 活动视图贯通：一个日窗驱动全部事实视图; 04 — Recap 窗口身份：缓存、生成锁与 SSE; 05 — Asking 窗口身份：问题缓存与提案提交不漂移.

**Status:** ready-for-agent

- [ ] 一次刷新只解析一次 Calendar Context，所有当次 day/week 请求、命令与 stream 都消费该不可变实例
- [ ] Browser timezone 变化只影响下一次刷新；已经发出的请求继续保留其原 Context，直到完成或取消
- [ ] selected date、timezone 或 refresh generation 已变化时，旧普通响应、App Detail 响应与 SSE event 均不能覆盖新状态
- [ ] `isToday`、时区标签、live/historical UI 状态以及生成按钮行为全部来自同一 Context，不各自读取当前时钟
- [ ] Report、Replay、Recap、Asking 与 Knowledge orchestration 不引入囊括全部操作的 Window Session；各模块只接收所需 Context
- [ ] orchestration tests 证明单次解析、跨消费者同一 identity、下一刷新时区切换和慢旧响应隔离
- [ ] 在普通日与 DST transition 日进行端到端刷新时，页面各区域展示和请求窗口完全一致
