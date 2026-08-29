# Local Calendar Window

Status: ready-for-human

## Problem Statement

Heartbeat 把所有事实以 UTC 保存，却让 Dashboard 的多个读取方分别解释“这天 / 这周”。现有日历
契约只携带一个 fixed offset，Frontend 和 Analytics 又分别用固定 24 小时或 7 × 24 小时推导窗口；
因此夏令时切换日可能漏掉或多读一小时，同一日期的 Report、Recap、Timeline、Asking、Usage 与
Key Frequency 也可能落到不同事实区间。用户旅行、修改浏览器时区或回看历史时区规则时，系统无法
证明页面上的各项内容讲的是同一个本地日历窗口。

Recap、Daily Questions 与生成锁当前主要以 UTC WindowStart 识别窗口，不能区分相同起点但不同
终点或 civil timezone 解释的窗口。旧契约还允许部分请求省略日期后回退到 UTC 当前时间，使一个
始终有日期选择器的 Dashboard 存在隐式的第二套日期语义。

## Solution

Dashboard 以 Local Calendar Window 统一解释用户选择的日期。每次刷新捕获当前浏览器 IANA civil
timezone，将必填的本地日期解析为不可变 Calendar Context：日窗口、本周窗口、是否为今天与时区
标签全部由同一 module 产生。日/周窗口按 civil calendar 的相邻本地起点形成半开区间，允许 DST
导致的 23/25 小时日与 167/169 小时周。

Browser 将版本、窗口类型、本地日期、IANA timezone 与完整 UTC 起止传给 Analytics。Analytics 用
独立 TZDB implementation 严格重算验证；一致后以同一 Instant Window 驱动 Report、Recap、Asking
及相关查询，并生成持久化 WindowKey。Frontend 与 Analytics 原子替换旧 fixed-offset 契约；旧缓存
作为可再生派生物按 miss 惰性重建，不保留两套日期语义。

## User Stories

1. As a Heartbeat owner, I want every Dashboard view for a selected date to use the same fact window, so that daily totals, timeline activity, recap text and questions agree.
2. As a Heartbeat owner in a DST-observing timezone, I want a spring-forward day to contain its real 23 hours, so that activity after the shifted clock is neither moved nor invented.
3. As a Heartbeat owner in a DST-observing timezone, I want a fall-back day to contain its real 25 hours, so that the repeated hour is not dropped.
4. As a Heartbeat owner, I want a week crossing a timezone transition to run from local Monday to the next local Monday, so that weekly totals use civil calendar semantics rather than a fixed duration.
5. As a Heartbeat owner, I want the selected date to be mandatory, so that Dashboard reads never silently fall back to a UTC date.
6. As a Heartbeat owner, I want “today” to be evaluated in my current browser timezone, so that the live and historical states match what I see locally.
7. As a traveling Heartbeat owner, I want the next refresh to reinterpret the selected date using my browser’s new timezone, so that hidden timezone snapshots do not survive travel.
8. As a Heartbeat owner, I want one refresh to keep a stable timezone snapshot, so that a timezone change during concurrent requests cannot tear the page across two windows.
9. As a Heartbeat owner, I want the Dashboard to show an accurate IANA timezone/offset label, so that I can understand which civil day I am viewing.
10. As a Heartbeat owner, I want a historical date affected by old timezone rules to resolve using those rules, so that archive playback remains truthful.
11. As a Heartbeat owner, I want an entirely skipped civil date to fail explicitly, so that the system never substitutes a neighboring date without telling me.
12. As a Heartbeat owner, I want all-device aggregation to retain the same Local Calendar Window as a single-device view, so that changing device scope does not change date semantics.
13. As a Heartbeat owner, I want the Activity Timeline to use the exact resolved day bounds, so that its scale naturally represents 23-, 24- and 25-hour days.
14. As a Heartbeat owner, I want App detail segments to reuse the Dashboard’s current day window, so that expanded Replay never queries a subtly different range.
15. As a Heartbeat owner, I want Keyboard Heatmap frequencies to use the same day window as usage and reports, so that event totals align with visible activity.
16. As a Heartbeat owner, I want Recap reads and generation to bind to the same full window, so that an LLM narrative cannot be generated for a different hour range than the page.
17. As a Heartbeat owner, I want Asking questions and proposals to bind to the original full window, so that an answer cannot be submitted against a reinterpreted date.
18. As a Heartbeat owner, I want the same local date viewed in different timezones to have distinct derived caches, so that one timezone’s recap does not describe another timezone’s facts.
19. As a Heartbeat owner, I want old fixed-offset Recap caches to remain non-destructive but miss the new identity, so that correctness improves without an eager LLM regeneration bill.
20. As a public Dashboard visitor, I want published reports and cached Recaps to use the same window contract as the owner view, so that visibility does not alter date semantics.
21. As a Dashboard maintainer, I want callers to pass one immutable Calendar Context instead of constructing offsets and end times, so that date bugs have locality.
22. As an Analytics maintainer, I want one verified Local Calendar Window module to feed all calendar projections, so that each query does not reimplement civil-time rules.
23. As an Analytics maintainer, I want arbitrary Instant Window queries to remain independent, so that usage and segment diagnostics are not forced into calendar semantics.
24. As an Analytics maintainer, I want a stable mismatch error when Browser and Analytics timezone rules disagree, so that a tzdata rollout problem is diagnosable instead of silently wrong.
25. As a database maintainer, I want cache and lock identity derived by Analytics after validation, so that caller-supplied keys never become trusted persistence identity.
26. As a release operator, I want Frontend and Analytics to replace the old calendar contract atomically, so that one deployment cannot serve two answers for the same date.
27. As a release operator, I want timezone-rule mismatch failures to identify which runtime is stale, so that updating the lagging Browser or Analytics environment is actionable.
28. As a test author, I want Browser and Analytics adapters to consume identical golden scenarios, so that cross-runtime timezone behaviour is verified rather than assumed.
29. As a future instant-range caller, I want to keep passing explicit instants without learning about Local Calendar Window, so that the new module does not widen unrelated interfaces.
30. As a future knowledge maintainer, I want Episode and Strand DateOnly values to remain timezone-free facts, so that a query-window refactor does not reinterpret confirmed knowledge dates.
31. As a frontend maintainer, I want stale responses correlated to their Calendar Context, so that a slow previous refresh cannot overwrite a newer date or timezone selection.
32. As a frontend maintainer, I want generated HTTP transport and handwritten Recap streaming transport to encode the same Calendar Window, so that code generation does not create a second date contract.

## Implementation Decisions

- Local Calendar Window is the canonical Dashboard term. It represents day and week fact windows derived from a required local calendar date and the current Browser IANA civil timezone.
- Day windows run from the first valid instant of the selected civil date to the first valid instant of the next civil date. Week windows run from local Monday to the next local Monday. All windows are half-open.
- A midnight timezone gap resolves to the first valid instant in that civil date. A civil date skipped in its entirety is an explicit error and is never normalized to a neighbor.
- The Frontend exposes a small Local Calendar Window module that resolves one immutable Calendar Context per refresh. The context owns strict local-date validation, timezone discovery, day/week instants, `isToday`, display label and a correlation identity.
- Report, Replay, Recap and Knowledge modules consume the Calendar Context; Local Calendar Window does not become a Window Session that owns all reads, commands and streams.
- Browser timezone changes become visible on the next refresh. Every operation started by one refresh retains its captured immutable context until completion or cancellation.
- Browser civil-time arithmetic uses a Temporal-compatible adapter with an explicit polyfill. Analytics uses NodaTime/TZDB. Neither runtime hand-writes offset arithmetic.
- The network interface is versioned and carries window kind, LocalDate, IANA timezone, UTC start and exclusive UTC end. A single `DateTimeOffset date` is no longer a calendar-window interface.
- Analytics strictly recomputes the expected window from LocalDate, timezone and kind. Exact disagreement with Browser instants returns a stable calendar-rules mismatch error; it never guesses which side to trust.
- After validation, downstream Analytics modules consume a resolved Instant Window and cannot derive a new end using a fixed duration.
- Daily and weekly Reports, Recap read/generate, Asking read/propose and public equivalents adopt the versioned calendar interface.
- Generic Usage and Segments retain their independent Instant Window interface. Dashboard transport adapters map the resolved day context to those existing semantics.
- Key Frequency uses the same resolved day context as Usage. Its event aggregation remains separate from segment semantics.
- Analytics generates the persistent WindowKey from a canonical versioned representation including kind, LocalDate, timezone and both UTC endpoints. Browser keys are correlation values only.
- Recap cache, Daily Questions cache, Recap generation lock and question-proposal lookup bind to Owner plus WindowKey. Persistence retains the civil metadata and UTC bounds for diagnosis.
- Legacy fixed-offset cache rows do not match the new WindowKey. They remain derived historical data and are regenerated lazily only when requested; there is no eager LLM backfill.
- The old fixed-offset calendar contract is atomically retired. No dual-read, fallback or open-ended compatibility path is introduced.
- Generated client transport is regenerated from the new OpenAPI contract. Recap SSE remains a handwritten adapter but consumes the same Calendar Context.
- Episode and Strand DateOnly serialization remains a separate module and decision. This work does not use Local Calendar Window to repair or reinterpret DateOnly values.
- Local Calendar Window errors are explicit and stable: invalid local date, unsupported timezone, nonexistent civil date and cross-runtime calendar-rules mismatch.
- The release unit includes Frontend, Analytics contract, cache schema/identity and generated client changes. Partial rollout is not supported.

## Testing Decisions

- Tests assert observable behaviour through the highest Local Calendar Window interfaces rather than private date helpers, raw timezone-library calls or internal hash functions.
- One language-neutral golden fixture set covers an ordinary day, spring-forward 23-hour day, fall-back 25-hour day, a week crossing each transition, a leap day, Sunday-to-Monday week selection, a midnight gap and an entirely skipped civil date.
- Browser adapter tests resolve each golden selection into exact UTC day/week bounds, `isToday` and labels using a deterministic clock/timezone adapter.
- Analytics module tests resolve the same fixtures independently and prove exact agreement with Browser-produced envelopes.
- Contract tests prove a valid envelope is accepted, malformed dates/zones/ranges are rejected, and a one-instant or rule-version disagreement returns the stable mismatch error without querying facts.
- Report tests cover half-open clipping at both endpoints on 23-, 24- and 25-hour days and a 167/169-hour week.
- Usage, Segments and Key Frequency adapter tests prove they receive the Calendar Context’s exact day endpoints while their generic Instant Window interfaces remain usable independently.
- Recap integration tests prove read, generation, projection, staleness and generation locking use the same WindowKey and resolved bounds.
- Daily Questions tests prove question generation and proposal submission remain bound to the original WindowKey and reject a context mismatch.
- Persistence tests prove identical canonical windows converge on one key, different timezone/end/kind/version values do not collide, and caller correlation keys are ignored for persistence.
- Migration tests prove legacy fixed-offset caches remain readable as legacy rows but never satisfy a new WindowKey, and no eager generation occurs during migration or startup.
- Frontend orchestration tests prove one refresh resolves exactly one Calendar Context, all reads reuse it, a timezone change affects the next refresh, and stale responses cannot overwrite newer state.
- Timeline and App-detail tests cover variable-length day bounds without assuming 86,400,000 milliseconds.
- Public and owner transport tests prove visibility changes routing/authorization only, not window semantics.
- Generated-client verification regenerates from the local Development OpenAPI endpoint, checks the contract diff, then runs type checking, tests and production build.
- Existing low-level tests that only lock obsolete fixed-offset helpers are replaced once equivalent interface-level coverage exists; tests are not layered indefinitely over retired implementation.

## Out of Scope

- Persisting an Owner home timezone or adding a timezone selector.
- Deriving calendar windows from a selected Device timezone.
- Preserving the timezone originally present when historical facts were collected.
- Month, quarter, rolling-N-day or arbitrary custom calendar ranges.
- Replacing UTC fact storage or rewriting historical fact timestamps.
- Reinterpreting Episode or Strand DateOnly knowledge fields.
- Fixing unrelated DateOnly generated-client serialization drift.
- Changing generic Usage or Segments into calendar-only queries.
- Reconciling unrelated zero-length Segment inclusion differences between query and projection paths.
- Eagerly regenerating legacy Recaps or Daily Questions.
- Supporting rolling deployments with old Frontend/new Analytics or new Frontend/old Analytics.
- Introducing a persistent user-visible Calendar Window entity; the context is a request/view interpretation, while only its WindowKey identifies derived caches and locks.

## Further Notes

- The governing decision is ADR-044. It amends the dedicated Report date contract, Recap cache/date contract and Recap generation-lock identity while preserving their other decisions.
- UTC database storage is already correct. This work changes only the mapping from a Browser civil calendar selection to the UTC fact interval consumed by Analytics.
- Strict validation intentionally turns timezone-database drift into an actionable request failure. It is preferable to generating a plausible but internally inconsistent archive.
- This specification is a multi-session build. Ticketing should preserve tracer-bullet delivery, make every blocking edge explicit and keep the repository runnable after each completed ticket.

## Comments

- 2026-08-29：Ticket 04 已完成；PRD 仍为 `ready-for-agent`，因为 03、05、06 与 07 尚未完成。
- 2026-08-29：Ticket 05 已完成；PRD 仍为 `ready-for-agent`，剩余门禁为 06 与 07。
- 2026-08-29：Ticket 06 已完成；PRD 仍为 `ready-for-agent`，剩余门禁为 07 的旧契约退休与原子发布验证。
- 2026-08-29：Ticket 07 的实现、自动验证与原子发布说明已完成；PRD 收口到
  `ready-for-human`，仅剩 maintainer 的本地 Dashboard 最终功能验收。验收通过后同步把 Ticket 07
  与本 PRD 置为 `done`。
