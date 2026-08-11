# ADR-034: App 表示跨平台产品，AppIdentity 表示平台观测身份

## Status: Accepted

## Date: 2026-08-11

## Context

现有 `App` 以 Windows 进程名唯一标识。跨平台后，同一产品会出现多个平台身份，例如 `win:code` 与 `mac:com.microsoft.vscode`；若把它们当成两个 App，Report、Matcher、Replay 与详情页都会错误分裂。反过来，如果采集端直接猜测产品归并，又会把不可验证的语义写进事实层。

## Decision

Heartbeat 将 `App` 定义为用户理解的跨平台应用产品，并新增 `AppIdentity(Key, AppId)` 表示平台可直接观测到的进程、bundle 或合成身份。AppIdentity 到 App 的映射是所有 Owner 共享的全局产品事实。摄入协议上传 `AppIdentityKey`，`ActivitySegment` 保存 `AppIdentityId`，Report、Matcher、Replay 与详情查询经 AppIdentity → App 聚合；AppIcon 仍挂在 App 产品下。

例如 `win:code` 与 `mac:com.microsoft.vscode` 同时映射到 App `vscode`。App Key 默认使用简短产品 slug，只在冲突时增加限定词。未知 AppIdentity 不按名称猜测合并，而是先建立一对一 provisional App，之后通过显式映射归并。选择这一模型而不是在平台 App 之上再加 Product/AppGroup，是为了保留平台观测事实，同时让产品归并只需调整 identity 映射，而不必批量改写历史段。

归并必须是事务化服务端领域操作：重绑 AppIdentity，并同步处理 provisional App、产品图标与依赖 App Key 的权威知识。它通过仅允许配置中 admin JWT `sub` 调用的管理 API 暴露，支持 dry-run、单事务执行与幂等重试，普通用户不能修改全局映射。presence 上传 `CurrentAppIdentityKey`，服务端保存当前 AppIdentity 并向读取方投影 App 产品。AppIcon 保持每个 Owner/App 一份，首个有效上传保留，避免 Windows 与 macOS 图标相互覆盖。

## Consequences

- ✅ Windows 与 macOS 的同一产品天然聚合，知识与报表只认稳定 App Key。
- ✅ ActivitySegment 保留真实 AppIdentity，调整映射即可重分类历史数据。
- ✅ 未知平台身份不会丢数据，也不会被名称启发式错误归并。
- ⚠️ 摄入、presence、图标上传、Matcher backfill 与前端 App DTO 都需迁移到双层身份。
- ⚠️ 全局映射修改影响所有 Owner，因此必须限制为管理操作。

## References

- [`shared/CONTEXT.md`](../../shared/CONTEXT.md) — App 与 AppIdentity 规范术语
- [`server/CONTEXT.md`](../../server/CONTEXT.md) — Analytics 中的产品聚合语义
- [ADR-018](./018-stable-segment-identity-snapshot-upload.md) — ActivitySegment 身份守卫
- [ADR-030](./030-collector-depth-declaration.md) — Matcher 与观测深度词汇
