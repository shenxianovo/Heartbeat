# ADR-016: Title Change Noise Control — Click Gating + Display-Layer Formatters

## Status: Accepted

## Date: 2026-07-01

## Context

ADR-015 (Strategy A) split a usage segment whenever App **or** Title changed, with no normalization — deliberately "先脏后治", to see what real title data looks like. Real data (observed on the owner's machine) revealed the title-noise problem has **several distinct shapes**, not one:

1. **Structured titles** — `apptitle.md - Heartbeat - Visual Studio Code` (file - project - appname), `Feed 和另外 4 个页面 - 个人 - Microsoft Edge`. The trailing app name is redundant; the useful parts are the head. This wants **parsing**, not suppression.
2. **High-frequency self-animation** — WindowsTerminal running Claude Code shows a spinner glyph (`✳`→`⠋`…) changing ~once/second. Same activity, but the title churns → segment explosion.
3. **Low-value foreground** — `explorer: 系统托盘溢出窗口` (the tray overflow popup). A real foreground window, semantically meaningless. Wants **filtering/classification**.
4. **Ambiguous change** — reading a novel where tabs differ only by chapter number (`第12章`→`第13章`). At the **string level this is identical to a spinner animation**, but semantically it is real progress, not noise.

### Why the collection agent cannot do content normalization

Type 4 is the killer. Distinguishing "spinner glyph churn (noise)" from "chapter number advancing (signal)" requires **semantic, per-app/per-window knowledge** — the agent has only the title string and cannot tell a decorative character change from a meaningful one. Worse:

- The same app+window class needs different rules (Terminal: PowerShell tab is static, Claude Code tab animates).
- The same app has tabs that are static vs. constantly-changing, unknowable in advance.

So any content-based normalization in the agent would be **lossy** (deleted info is unrecoverable — e.g. mistaking chapter numbers for noise), **require a new release to change**, and be forced to embed per-app semantics. This contradicts ADR-012's "store the raw stream so any post-hoc analysis stays possible" and the owner's explicit choice of **lossless** handling. **Rejected.**

### The insight that works: gate on input, not on content

Titles that change **while the user is not interacting** are almost always the program animating itself (noise). A tab/page switch is almost always the result of a **click** (on a tab, link, bookmark). This is a **content-agnostic** signal the agent *can* use.

Naive "any input" gating fails one observed case: typing in Claude Code while its spinner animates — there **is** input (typing) and the title churns, so it would pass. Narrowing to "keys that could switch windows" fails too: our per-event input stream has **no combo-key concept** (`Ctrl+Tab` arrives as two independent KeyDowns), and "which keys navigate" is app-specific and unbounded.

The clean cut: **gate on mouse/touchpad clicks only.** Typing (of any kind) is excluded, so the Claude-Code-spinner-while-typing case goes silent. Touchpad taps arrive as `WM_LBUTTONDOWN` (already captured). No combo detection, no app-specific key sets, no character classification.

Accepted losses (segment *granularity* only — never data; the raw title is still stored):
- Keyboard-only tab switching (`Ctrl+Tab`) and mouse side-button (back/forward) navigation produce no click → that switch is folded into the previous segment. Acceptable; if data later shows this is frequent, "non-character keys" can be added to the gate.
- Auto-advancing content (autoplay video, auto-paging novel) has real progress but no input → folded into one segment. Acceptable and arguably more intuitive ("watched 40 min" vs. many 5-min slivers).

## Decision

Two layers, strictly separated. The agent handles **volume** (never content); the display layer handles **all semantics**, losslessly and without a release.

### 1. Collection agent — click gating (content-agnostic)

- A lightweight shared signal `IInputActivitySignal` exposes `LastClickTicks` (monotonic). The mouse-click path (`WM_LBUTTONDOWN/RBUTTONDOWN/MBUTTONDOWN`, which already covers touchpad taps) updates it. Scroll and keyboard do **not**.
- In `AppMonitorService`, when a foreground change is **App change** → always split (as today). When it is **Title-only change** (same App) → split **only if** a click occurred within the gate window `X` before the event; otherwise update the tracked current title in place **without** starting a new segment.
- **Gate window `X = 1s`** (first cut, tunable). Rationale: a click almost always *precedes* the title change (click → navigation → title updates), but the gap varies (slow page loads). 1s covers the common gap without crediting stale clicks.
- **Lossless**: gating decides *whether to open a new segment*, never alters a title. Stored segments always carry the original, complete title.
- The tracked title is always updated to the latest value even when not splitting, so if the segment is later closed (app switch / flush) it records the most recent real title.
- 跨平台窗口事件 seam 区分 **AppIdentity 激活**、**focused-window 切换**与**同窗标题变化**。前两者属于明确转场，始终切段；只有同窗标题变化需要点击门控。
- macOS 已获 Accessibility、未获 Input Monitoring 时，仍记录 focused-window 切换及其标题，但忽略无法点击门控的同窗标题变化；权限不足不阻塞 App-only 采集。

### 2. Display layer — per-app formatters (lossless, iterable)

- The agent stores raw complete titles (unchanged from ADR-015). Parsing/classification happens **only at display time**.
- A formatter registry keyed by the cross-platform product **App.Key**, each entry a **function** `(rawTitle: string) => { primary: string; secondary?: string }`. A function (not a declarative regex table) is chosen deliberately: the observed cases already need in-app branching (Terminal: Claude Code vs. PowerShell; browser: static vs. churning tab), and once more data accrues the owner will want richer parsing (multi-part splits, conditional logic, possibly semantic checks later) — a function has no expressiveness ceiling and avoids a future migration away from a table. Lookup falls back to the raw title when no formatter is registered for the App.
- Examples: VSCode formatter strips the ` - Visual Studio Code` tail and returns `{ primary: file, secondary: project }`; Edge formatter extracts the page name; explorer tray-popup titles classify to a "system/desktop" bucket; a novel-site formatter recognises chapter progression; the WindowsTerminal formatter collapses `✳ Claude Code` / `⠐ Claude Code` (any spinner glyph) to `{ primary: 'Claude Code' }`.
- **Spinner collapse is display-layer fallback, not the primary defense.** Click gating (layer 1) suppresses the vast majority of spinner churn at the source (spinner changes without a click never split). But gating cannot be 100% clean — e.g. a click landing within the gate window while an animation runs lets a few spinner-titled segments through. The formatter's many-raw-titles → one-friendly-name mapping absorbs those stragglers at display time.
- Formatters are frontend code: wrong rules or new apps → edit + recompute, no agent release, raw titles always intact.

## Consequences

- ✅ Claude-Code-spinner-while-typing (the observed worst case) goes silent — typing isn't a click, so title churn during typing never splits.
- ✅ Program self-animation, background title refresh, autoplay progress — all no-click → suppressed at the source, so **segment explosion is cut in the agent** (not just hidden at display).
- ✅ Fully lossless: agent never edits titles; all semantics live in display-layer formatters that are iterable without a release, honoring ADR-012.
- ✅ `IInputActivitySignal` is reusable — the same "last input time" is what a future soft-idle detector would need.
- ⚠️ Keyboard-only and side-button navigation without a click fold into the previous segment (granularity loss, not data loss). Revisit by adding non-character keys to the gate if data warrants.
- ⚠️ Auto-advancing content folds into one segment (accepted, arguably better).
- ⚠️ Gate window `X` is a guess (1s); a very slow page load whose title updates after >1s from the click would be treated as noise. Tunable after observing data.
- ⚠️ New cross-collector coupling: `AppMonitorService` now reads an input signal. Kept minimal (one timestamp), injected via `IInputActivitySignal`.
- ⚠️ 窗口监视 adapter 必须保留转场种类，不能再把 focused-window 切换与同窗标题变化压成同一个模糊事件。

## References

<!-- Filled in as implementation lands -->

- `desktop/Heartbeat.Agent/Utils/IInputActivitySignal.cs` — shared last-click signal (pending)
- `desktop/Heartbeat.Agent/Utils/LowLevelInputHook.cs` — click path updates the signal (pending)
- `desktop/Heartbeat.Agent/Services/AppMonitorService.cs` — title-change click gating (pending)
- `frontend/src/` — per-app title formatter registry (pending)
- [ADR-015](./015-window-title-segment-dimension.md) — title as segment dimension (Strategy A) this refines
- [ADR-012](./012-input-event-tracking.md) — raw-stream / lossless principle
