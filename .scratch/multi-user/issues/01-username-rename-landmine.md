# Username 改名联动雷（AuthService 加改名功能时必须处理）

Status: decided (2026-07-17 grilling session), ready to implement

## 背景

多用户设计（2026-07-15 grilling session）决定：URL 用 username（`base/u/:username`），
Heartbeat 本地 `User.Username` 是 AuthService 数据的缓存，只在本人带 JWT 请求时按 `sub` 定位刷新。

当前 AuthService **没有改名端点**（`Username` 唯一索引，无 update route），所以 username 事实不可变，
串号不会发生。决策（问题 5 选 B）：**不为"未来可变"预建隔离**，符合"不预建"原则。

## 雷的形状

一旦 AuthService 增加"修改 username"功能：

1. **旧链接腐烂**：`base/u/alice` 改名后本地缓存 stale，本人不登录就一直指向旧名。
2. **串号**：若 AuthService 允许旧名被他人重新注册，`base/u/alice` 本地查到老用户的行，
   AuthService 上 alice 已是新人——同一 URL 两个身份。

## 决策（2026-07-17）

- **AuthService 侧：立即释放（GitHub 模式）**。旧名改名后立刻可被任何人注册,
  不做冻结期、不做 tombstone（永久保留被否决:为不存在的命名空间压力引入状态机,
  违反"不预建"）。GitHub 的 popular namespace retirement 例外不适用——
  `/u/:username` 是看板主页,不是供应链坐标。
  防线主体由此**从 AuthService 移到 Heartbeat provisioning**（推翻本 issue 原建议）。
- **AuthService 侧：改名吊销该用户所有 session**（旧 token 烧着旧 `preferred_username`,
  吊销换干净）;**不限频**（2026-07-17 决定:个人项目规模,抢注倒卖游戏不成立）。
- **Heartbeat 侧：ProvisionAsync 驱逐（eviction）**。回写 username 前若发现该名被
  "别的 sub"的行占用,将该 stale 行的 Username 改为 `~{该行.Id}` 占位,同事务保存。
  `~` 不在上游合法字符集（`^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){1,38}$`）,占位值
  永不撞真名;`{Id}` 唯一,占位值间永不互撞。被驱逐者下次带 JWT 请求经 sub-first
  回写自愈,期间其看板无门牌号（`/u/旧名` 归新主,`/u/新名` 404 直到本人登录）——
  与懒建供给"本人激活"语义一致。
- **Heartbeat 侧：`ResolveByUsernameAsync` 拒绝 `~` 开头的查询**——否则知道 sub 的人
  可在驱逐窗口期经 `/u/~{sub}` 访问被驱逐者的公开看板。一行 guard。
- **接受的残留窗口（两个）**:
  1. 新主抢注旧名后、首次登录 Heartbeat 前,`/u/旧名` 仍解析到原主的行
     （原主 opt-in public 的自有数据,无越权泄露,但访客可能误认身份）。
     懒建体系下只能靠回源消除,回源已被 ADR-025 退役——与 GitHub redirect
     被抢注顶掉同构,知情接受。
  2. 无状态 JWT 杀不掉:改名吊销 session 后,已签发 token 活到 exp——
     session JWT 15 分钟,OIDC access token **1 小时**（OpenIddict 默认,
     未调 `SetAccessTokenLifetime`；2026-07-17 决定暂不对齐到 15 分钟,
     留给"token 签发统一到 OpenIddict"工程一并处理）。窗口内旧 token 携旧名
     请求 Heartbeat 会回写旧名、甚至反向驱逐新主——ping-pong 由 sub-first
     回写自愈,知情接受。

### 被否决的备选

- 让新主的 provisioning 失败:新主在 AuthService 合法持有该名,登录必须成功。
- 去掉本地唯一索引、按 LastSeenAt 解析:两行同名成持续歧义态,匿名查询入口
  从确定性查找退化为启发式。
- AuthService webhook 通知改名:懒建已能自愈,为此引入跨服务投递可靠性问题,
  违反"不预建"（ADR-025 已否决过一次）。

## 关联

- **ADR-027**（`docs/adr/027-username-rename-eviction.md`）——本 issue 的决策正式化
- AuthService 侧工作项：`AuthService/.scratch/username-rename/issues/`（01 注册表单修复、02 改名端点）
- `server/CONTEXT.md` → User Provisioning 词条（sub-first 规则）
- ADR-024（OIDC 认证，`preferred_username` claim 来源）
- AuthService: `backend/AuthService/Entities/User.cs`（Username 唯一索引）
