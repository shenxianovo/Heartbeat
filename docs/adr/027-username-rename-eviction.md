# ADR-027: Username Rename Propagation — Eviction on Provision

## Status: Accepted (amends ADR-025)

## Date: 2026-07-17

(Design session decision; implementation commits to be appended as they land.
Upstream AuthService rename feature designed the same session, see
`AuthService/.scratch/username-rename/`.)

## Context

ADR-025 的 sub-first 规则把 username 定义为"可刷新的显示缓存 + 匿名查询入口"，
并显式否决了为改名预建防串号隔离——当时的前提是 AuthService 无改名端点、
username 事实不可变，防线（若将来需要）的主体在上游（记雷代替，
`.scratch/multi-user/issues/01-username-rename-landmine.md`）。

现在 AuthService 正式增加用户自助改名。上游同场决策采用 **GitHub 模式**：

- 旧名**立即释放**，可被任何人注册。永久保留被否决——为不存在的命名空间压力
  引入 tombstone 状态机，违反"不预建"；冻结期同理。GitHub 的 popular namespace
  retirement 例外不适用：`/u/:username` 是看板主页，不是供应链坐标。
- 改名吊销该用户全部 session；不限频。

由此前提反转：**防线主体从 AuthService 移到 Heartbeat 的 provisioning 写路径**。
具体碰撞：A 改名释放 `alice`，B 抢注 `alice` 并首次登录 Heartbeat——
`ProvisionAsync(B.sub, "alice")` 撞本地 `Users.Username` 唯一索引，
因为 A 的 stale 行还占着这个名字（A 一直没登录）。B 在上游合法持有该名，
登录必须成功。

### 被否决的备选

- **让新主的 provisioning 失败**：新主无辜且无法自救。
- **去掉唯一索引、按 LastSeenAt 解析**：两行同名成持续歧义态，匿名查询入口
  从确定性查找退化为启发式，污染 CONTEXT.md 词义。
- **AuthService webhook 通知改名**：懒建已能自愈，为此引入跨服务投递可靠性
  问题，违反"不预建"（ADR-025 已否决过一次）。

## Decision

**驱逐（eviction）：供给回写时，同名异 sub 的 stale 行被改为占位值，被驱逐者
下次登录自愈。**

1. `ProvisionAsync` 回写 username 前，查询 `Username == username && Id != userId`
   的行；存在则将其 `Username` 改为 **`~{该行.Id}`**，与本次 upsert 同事务保存。
   - `~` 不在上游合法字符集（`^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){1,38}$`，
     AuthService `UsernameValidator.cs`）→ 占位值永不撞真名；
   - `{Id}`（sub）唯一 → 占位值之间永不互撞。零新状态、零配置。
2. `ResolveByUsernameAsync` 拒绝 `~` 开头的查询——封掉驱逐窗口期经
   `/u/~{sub}` 探测被驱逐者公开看板的路径。
3. **自愈闭环**：被驱逐者下次带 JWT 请求经既有 sub-first 回写拿回当前名字。
   期间其看板无门牌号（`/u/旧名` 归新主，`/u/新名` 404 直到本人登录）——
   与懒建供给"本人激活"语义一致。

### 知情接受的残留窗口

1. 新主抢注旧名后、首次登录 Heartbeat 前，`/u/旧名` 仍解析到原主的行
   （原主 opt-in public 的自有数据，无越权泄露，但访客可能误认身份）。
   懒建体系下只能靠回源消除，回源已被 ADR-025 退役——与 GitHub redirect
   被抢注顶掉同构。
2. 无状态 JWT 杀不掉：改名吊销 session 后已签发 token 活到 exp
   （session JWT 15 分钟，OIDC access token 1 小时——OpenIddict 默认，
   对齐到 15 分钟留给上游"token 签发统一到 OpenIddict"工程一并处理）。
   窗口内旧 token 携旧名请求会回写旧名、甚至反向驱逐新主——ping-pong
   由 sub-first 回写自愈。

### 部署顺序约束

**Heartbeat 的驱逐必须先于 AuthService 改名功能上线**，否则碰撞窗口内
新主登录直接撞唯一索引报错。

## Consequences

- ✅ sub-first 规则延伸到 username 全局可复用的世界：名字在 sub 之间流转，
  身份永不串——一致性收敛点在 Heartbeat 自己的写路径上，零新增同步机制。
- ✅ ADR-025 的"记雷代替预建"策略闭环：雷被触发时按 issue 01 的决策拆除。
- ⚠️ 驱逐期间被驱逐者的公开看板不可达，直到本人登录（主动改名的自然代价）。
- ⚠️ 残留窗口 1/2 如上，均自愈、均知情接受。
- ⚠️ ADR-025 中"username 事实不可变"的表述自本 ADR 起作废。

## References

- [`server/Heartbeat.Server/Services/UserService.cs`](../../server/Heartbeat.Server/Services/UserService.cs) — ProvisionAsync 驱逐落点、ResolveByUsernameAsync guard 落点
- `.scratch/multi-user/issues/01-username-rename-landmine.md` — 决策过程全记录（含被否备选）
- [ADR-025](./025-multi-user-visibility-identity.md) — sub-first 规则与"记雷代替预建"的原始决策
- `server/CONTEXT.md` — User Provisioning 词条（已更新驱逐语义）
- AuthService: `Common/UsernameValidator.cs`（字符集）、`.scratch/username-rename/`（上游工作项）
