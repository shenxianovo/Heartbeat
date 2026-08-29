# ADR-044: 浏览器定义 Local Calendar Window，Analytics 严格验证

## Status: Accepted（amends [ADR-006](./006-dedicated-report-endpoints.md) 日/周报日期契约、[ADR-023](./023-recap-cloud-llm-projection.md) §4/§5、[ADR-042](./042-recap-streaming-generation.md) §7）

## Date: 2026-08-28

## Context

Heartbeat 的事实时间全部以 UTC 保存，但“这天 / 这周”仍需先把用户所见的本地日历映射成一段
UTC instant。Shared Kernel 已把这个解释权交给当前浏览器时区；现有契约却只传一个
`DateTimeOffset date`，Analytics 再以同一个 offset 加固定 24 小时或 7 × 24 小时。Dashboard
同时在多处用请求当下的 offset、`start + 86400000` 和不同的 `Date` 解析方式构造窗口。

单个 UTC offset 不是 civil timezone。夏令时切换日可能是 23 或 25 小时，跨切换的周可能是
167 或 169 小时；浏览器宿主与 Analytics 容器的 IANA 时区数据库也可能因发布节奏不同而短暂
漂移。结果是 Report、Recap、Timeline、Asking、Usage 与 Key Frequency 可能对同一个所选日期读取
不同事实，旧的 `(OwnerId, WindowStart)` 缓存与生成锁也无法区分相同起点但不同终点或 civil 解释
的窗口。

需要保留两类真实而不同的查询：Dashboard 按本地日历回顾；Usage / Segments 等能力也允许任意
instant range。把后一类强行改成日历查询会降低 interface 的 depth；让每个 caller 各自把日历
转成 instant range 又会失去 locality。

## Decision

### 1. Local Calendar Window 是 Dashboard 对所选日期的共同解释

Dashboard 将 **Local Calendar Window（本地日历窗口）** 定义为：以必填的 Gregorian
`yyyy-MM-dd` 和每次刷新时捕获的当前浏览器 IANA civil timezone，解析出的日窗口与周窗口。

- 日窗口是 `[所选 civil date 的第一个有效 instant, 下一 civil date 的第一个有效 instant)`；
- 周窗口以周一开始，是 `[所在周周一的第一个有效 instant, 下周一的第一个有效 instant)`；
- 窗口一律半开，不以固定毫秒数推导终点；
- 午夜落入时区 gap 时取该 civil date 的第一个有效 instant；整个 civil date 不存在时明确失败，
  不静默归到相邻日期；
- 日期不可省略，不允许退回 Analytics 的 UTC 当前日期；
- 同一次刷新只解析一个不可变 Calendar Context，Report、Recap、Timeline、Asking、Usage、
  Segments 与 Key Frequency 共享它；浏览器时区变化在下一次刷新生效；
- Episode / Strand 的 DateOnly 是没有时区的知识事实，不进入 Local Calendar Window；任意
  Instant Window 也保持独立 interface。

Frontend 的 Local Calendar Window module 只暴露从所选日期解析不可变 Calendar Context 的小
interface；它拥有严格日期解析、当前 IANA timezone、日/周 instant、`isToday` 与时区标签。
Report、Replay、Recap、Knowledge 等 module 各自消费同一个 context，不把所有读取、command 与
stream 合并成一个巨型 Window Session。

### 2. 浏览器发送完整 civil 含义与 instant 窗口

Local Calendar Window 通过现有 Dashboard → Analytics HTTP seam 发送版本化 envelope，至少包含：

```text
Version
Kind                 day | week
LocalDate            yyyy-MM-dd
TimeZone             IANA timezone id
Start                UTC instant
EndExclusive         UTC instant
```

浏览器负责选择当前 IANA timezone 并解析完整 Calendar Context；单个 `DateTimeOffset date` 契约
退役。Analytics 使用 NodaTime / TZDB 按 `LocalDate + TimeZone + Kind` 严格重算，并要求结果与
`Start / EndExclusive` 完全一致；不一致返回稳定的 `calendar_rules_mismatch`，不猜测采用任一侧
结果。验证通过后，事实过滤只消费该半开 Instant Window，不能再次用固定时长推导终点。

日报、周报、Recap 读取与生成、Asking 读取与提案必须携带相同的版本化窗口。通用 Usage / Segments
Instant Window endpoint 保留；Dashboard transport adapter 把 Calendar Context 的日窗口映射为其
`start / end`，其他 instant-range caller 不需要学习日历语义。

Browser 使用 Temporal interface 并携带显式 polyfill，不把正确性依赖于原生支持的发布节奏；
Analytics 使用 NodaTime / TZDB。两个 implementation 共享跨 runtime golden fixtures，而不是共享
某一语言的日期代码。

### 3. Analytics 验证后生成持久化 WindowKey

Analytics 在严格验证 envelope 后生成版本化、确定性的 WindowKey；canonical 输入至少包括
`Version / Kind / LocalDate / TimeZone / Start / EndExclusive`。浏览器可以使用临时 correlation
key 防止旧响应覆盖新页面状态，但 caller 提供的 key 不能成为数据库身份。

Recap 缓存、Daily Questions 缓存、Recap generation lock 与问题提案的窗口关联均使用
`(OwnerId, WindowKey)`；同时保存 LocalDate、IANA timezone 与 UTC 起止供诊断。相同
`yyyy-MM-dd` 在不同时区对应不同 WindowKey，因为它们消费的事实区间和本地时间叙事可能不同。

旧 fixed-offset 缓存是可再生派生物，不能可靠推断原浏览器的 IANA timezone 或正确终点。迁移后
它们不命中新版 WindowKey，按 cache miss 惰性重建；不猜测回填、不主动批量调用 LLM。

### 4. 原子替换旧日期契约

Frontend 与 Analytics 同版发布，原子替换旧 `DateTimeOffset date` 日历契约，不保留双读、fallback
或长期兼容分支。Heartbeat 当前是单用户自部署系统，日历读取方由同仓库 generated client 与手写
SSE adapter 控制；为滚动异构客户端长期背负两套窗口语义不成比例，也会让同一天继续存在两个
答案。契约切换后重新生成 frontend client，Recap SSE 继续保留手写 adapter，不强塞进 codegen。

## Consequences

- ✅ UTC 事实存储不变；变化只发生在“本地日历 → UTC instant window”的解释与验证。
- ✅ DST 日、跨 DST 周与历史时区规则得到明确语义，Report、Recap、Timeline 与 Asking 消费同一
  窗口。
- ✅ Local Calendar Window 的小 interface 给多个 caller 提供 leverage；日期、时区、窗口与展示
  标签获得 locality。
- ✅ 通用 Instant Window 保持独立，任意时刻查询不被迫学习日历语义。
- ✅ 完整 WindowKey 使缓存、生成锁与问题提案绑定真实窗口；切时区不会误命中另一组事实。
- ⚠️ Browser Temporal 与 Analytics NodaTime 是两份 implementation，必须以同一组普通日、23h、
  25h、跨 DST 周、闰日和跳日 fixture 锁住一致性。
- ⚠️ 浏览器与 Analytics 的时区规则不一致时请求会失败，部署者需更新落后的一侧；系统不会用
  “大概正确”的窗口继续生成叙事。
- ⚠️ 旧 Recap 缓存不会命中新 WindowKey；再次回看时可能重新消耗一次 LLM token，但不会启动
  主动批量重生成。
- ⚠️ 旧日期契约是破坏性退役，Frontend 与 Analytics 必须作为一个发布单元切换并重新生成 client。

## References

- [`shared/CONTEXT.md`](../../shared/CONTEXT.md) — UTC 存储与浏览器时区解释权
- [`frontend/CONTEXT.md`](../../frontend/CONTEXT.md) — Local Calendar Window 词条
- [ADR-006](./006-dedicated-report-endpoints.md) — 日/周报与通用 Usage 查询并存
- [ADR-023](./023-recap-cloud-llm-projection.md) — Recap 日窗口与缓存
- [ADR-031](./031-hierarchical-strand-episode-teaching-loop.md) — Asking、Episode 与 Strand 知识语义
- [ADR-042](./042-recap-streaming-generation.md) — Recap 读取 / 生成与窗口生成锁
- [`shared/Heartbeat.Core/DateRange.cs`](../../shared/Heartbeat.Core/DateRange.cs) — 被替换的 fixed-offset 窗口计算
- [`frontend/src/api/index.ts`](../../frontend/src/api/index.ts) — 被收敛的日期序列化与 transport adapters
