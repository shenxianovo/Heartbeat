# Username 改名联动雷（AuthService 加改名功能时必须处理）

Status: needs-triage

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

## 触发时必须做

- AuthService 侧：决定旧名是否可复用（建议：冻结期或永久保留），这是防线的主体。
- Heartbeat 侧：改名传播机制（本人登录时 sub-first 回写已覆盖；考虑 webhook 或
  匿名查询时的 stale 检测）；本地 `Users.Username` 加唯一约束。

## 关联

- `server/CONTEXT.md` → User Provisioning 词条（sub-first 规则）
- ADR-024（OIDC 认证，`preferred_username` claim 来源）
- AuthService: `backend/AuthService/Entities/User.cs`（Username 唯一索引）
