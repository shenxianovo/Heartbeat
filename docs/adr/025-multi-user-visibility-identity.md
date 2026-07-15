# ADR-025: Multi-User Visibility & Identity Model

## Status: Accepted

## Date: 2026-07-15

(Design session decision; implementation commits to be appended as they land.)

## Context

CONTEXT-MAP 定位把商业化列为"被保留的选项"，并靠三条不变量保持多用户之门敞开。
现在正式走向多用户：用户看板 URL 采用 `/u/:username`（前缀式，Reddit 同款；
放弃 GitHub 式裸 `/:username`——裸命名空间让每个未来的顶层系统路由都要
与用户名抢词，GitHub 为此维护数百词的保留名单，`/u/` 前缀让该问题不存在），
访问语义必须反转。

单用户时代的三个前提在多用户下失效：

1. **读路径全公开。** `PublicUserController` 是看板唯一读路径——devices、日报周报、
   usage、segments、设备实时在线、键频，全部无鉴权按用户名可读。单用户下是"给自己
   分享用的玩具"，多用户下是数据泄露：窗口标题含文档名/聊天对象/URL，等于直播生活。
2. **匿名读驱动用户供给。** `UserService.ResolveByUsernameAsync` 在本地查不到用户时
   回源 AuthService 并自动建行——任何路人访问任意 URL 都能 provision 用户。多用户下
   这是爬虫刷空行 + 用户名枚举向量，且供给语义主体错位（路人激活而非本人激活）。
3. **App 图标是全局可写共享状态。** `UploadIcon` 按 AppName 对全局 `AppIcon` upsert，
   后写胜——任何登录用户可覆盖所有人看到的任意应用图标（跨租户涂鸦）。

数据隔离本身（`Device.OwnerId` 过滤，CONTEXT-MAP 不变量 2）核查下来是干净的，无需改动。

### 被否决的备选

- **可见性 = 精选聚合视图**（public 只暴露日/周总时长、应用排行、键盘热力图，不给
  title/presence/segments）：更安全，但把"公开什么"的策略决策前置。已决定后续用
  自定义看板的每卡片可见性配置（"先 A 后 C"）承接这个灵活性，现在做是把三个功能焊死成一个。
- **显式注册端点**：有"接受条款"钩子，SaaS 迟早要，但不是多用户的必需——留给隐私条款场景。
- **AuthService webhook 同步供给**：最干净但要求上游改动，懒建已够用。
- **App 整个 per-owner**：隔离最彻底，但 `ActivitySegments.AppId` FK 按 owner 重映射是
  伤筋动骨的迁移，而 App 只是 `Id/Name` 归一化字典，读侧已有 OwnerId 过滤，共享无泄露。
- **为 username 改名预建防串号隔离**：AuthService 当前无改名端点，username 事实不可变；
  串号前提（旧名被他人复用）由 AuthService 控制，防线不在本仓。违反"不预建"原则，
  记雷代替（`.scratch/multi-user/issues/01-username-rename-landmine.md`）。

## Decision

**默认 private 的看板可见性 + 本人触发的懒建供给 + sub-first 身份规则 + 图标写权按 owner 隔离。**

### 1. Dashboard Visibility — `User.IsPublic`，默认 false

- `PublicUserController` 每个端点前置检查：private 一律 **404**（不是 403——不泄露
  用户名存在性，GitHub 同款语义）。
- 本人经 JWT（`sub == User.Id`）读自己的数据走鉴权路径，不受 `IsPublic` 影响。
  前端自己看自己目前走的就是 public 端点，鉴权自读路径必须先落地再上锁，否则 owner 锁死自己。
- 当前阶段 public = 全保真放行现有端点（决策"先 A 后 C"）：待自定义看板落地后升级为
  每卡片可见性配置，全保真公开届时退役。

### 2. User Provisioning — 懒建，本人首次带 JWT 触发

- 带 JWT 请求 upsert User 行：`Id = sub`，`Username = preferred_username`，默认 private。
- 匿名按用户名读取**只查本地 Users 表**，查不到即 404：`FetchFromAuthServiceAsync`
  退役，Heartbeat 不再回源 AuthService 解析用户名。
- Heartbeat 无"注册"概念：账号归 AuthService，登录即存在。

### 3. Identity — sub-first 规则

- 带 JWT 请求一律用 `sub`（AuthService User.Id，UUIDv7，永不变）定位 User 行，
  并回写 `preferred_username`——username 只是可刷新的显示缓存 + 匿名查询入口。
- `Users.Username` 加唯一索引。
- 用户看板路由挂 `/u/` 前缀，与顶层系统路由（`/`、`/settings`、`/callback`……）
  命名空间隔离——**无需维护保留用户名名单**（一个叫 `settings` 的用户其看板是
  `/u/settings`，与 `/settings` 无冲突）。

### 4. AppIcon — 写权按 owner 隔离

- `AppIcon` 加 `OwnerId`，唯一键 `(OwnerId, AppId)`；`UploadIcon` 按 JWT owner 写，
  图标读取端点带 owner 上下文（看谁的看板就取谁的图标）。
- `App` 维持全局字典（进程名归一化维度），不加 OwnerId。

## Consequences

- ✅ 可见性语义反转后，"分享自己的看板"成为 opt-in 特性而非默认暴露；404 语义不泄露存在性。
- ✅ 供给收敛到本人激活：无爬虫刷行、无用户名枚举、`IsPublic` 开关有明确的落点（User 行随登录必然存在）。
- ✅ sub-first 把身份钉在不变键上，username 改名（未来）只影响 URL 不串数据。
- ✅ 图标涂鸦向量关闭；App 字典不动，段表 FK 零迁移。
- ⚠️ 全保真 public（阶段 A）意味着 opt-in 公开的用户暴露窗口标题与实时在线状态——
  知情开启可接受，但升级到每卡片可见性（C）前不宜宣传"分享"功能。
- ⚠️ 老的公开链接（如已分享的 `base/shenxianovo`）在 owner 开启 IsPublic 前会 404——一次性行为变更。
- ⚠️ username 改名雷仍在（AuthService 加改名功能时联动，见 issue）；SaaS 形态问题
  （ADR-012 键击流的多用户合法性）**未被本 ADR 解决**，收真实用户前必须另行决策。
- ⚠️ 图标按 owner 冗余存储（每人一份 chrome 图标，几 KB/人/应用），可接受。

## References

- [`server/Heartbeat.Server/Controllers/PublicUserController.cs`](../../server/Heartbeat.Server/Controllers/PublicUserController.cs) — 加 IsPublic 前置检查的读路径
- [`server/Heartbeat.Server/Services/UserService.cs`](../../server/Heartbeat.Server/Services/UserService.cs) — 懒建供给改造点（回源逻辑退役）
- [`server/Heartbeat.Server/Entities/User.cs`](../../server/Heartbeat.Server/Entities/User.cs) — IsPublic 字段落点
- [`server/Heartbeat.Server/Entities/AppIcon.cs`](../../server/Heartbeat.Server/Entities/AppIcon.cs) — OwnerId 落点
- [`server/Heartbeat.Server/Controllers/AppController.cs`](../../server/Heartbeat.Server/Controllers/AppController.cs) — UploadIcon 写权隔离
- [`frontend/src/router/index.ts`](../../frontend/src/router/index.ts) — `/u/:username` 路由、landing 页
- `server/CONTEXT.md` — Dashboard Visibility / User Provisioning 词条
- `.scratch/multi-user/issues/01-username-rename-landmine.md` — username 改名联动雷
- [ADR-024](./024-oidc-jwt-authentication.md) — sub / preferred_username claim 的来源
- [ADR-012](./012-input-event-tracking.md) — 键击流单用户前提（本 ADR 未解决，SaaS 形态待决）
