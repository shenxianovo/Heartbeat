# Collector 与 Analytics 的观测语义解耦

Status: done

用户 2026-09-11 授权：先提交五表存储，再完成上一轮指出的语义解耦，继续当前会话。
存储提交基线：90273f9。沿用已确认的 HTTP → PostgreSQL → 查询及 Collector → Runtime → ACK/重放验证路径。

## 范围

- Collector 在 Fact 中显式给出 Aspect；Runtime 透明保管、重传和确认该字段。
- Analytics 按 Aspect 契约解释其支持的活动/输入分析，Source 只作来源、明确来源筛选及现有知识声明身份。
- Dashboard 按 Aspect 选视图、关联页面并保留真实 Source；Experience/Usage 查询透出语义。
- 未知 Aspect/结果可原样存取，不被猜成活动或输入。已有活动和输入 JSON 形状保持；不重定时长口径。
- System、Browser、VRChat 都发布显式 Aspect；协议能力防止旧端忽略该字段后 ACK。
- 历史数据与未升级缓存仅在明确的旧入口解释，既有 identityKey 规范化仍可重放；不给新语义制造 legacy 分类或 Gap。
- 知识深度声明继续遵循 ADR-030；本次不把 Source 分组、用户 Matcher 身份误改成 Aspect。

## 验收

- [x] 新来源采用已知 Aspect，无需改 Analytics 即可进入相应分析；伪装成已知 Source 的未知 Aspect 不被误算。
- [x] 未知 Aspect/Result 完整存取，原有兼容流重放与修订规则保持。
- [x] Collector、SDK、Runtime、HTTP、数据库的 Aspect 保管与 ACK 一致，旧端不能静默忽略。
- [x] 查询/报表按观测契约选择，展示及深度解释仍由现有视图与声明负责。
- [x] 追加迁移、相关集成及全仓回归、规范/规格审查和文档收口完成。

不操作业务库，不恢复暂停的副本/资源演练，不部署。

## 实施与验证记录

- 基线存储已先提交 `90273f9`；本层变更独立成批，不改写已发布迁移。
- 显式 Aspect 贯通三个第一方 Collector、SDK、Runtime、HTTP、Facts、分析与 Dashboard。
- Source 保留来源及声明身份；未知结果不经过活动规范化。详细契约及兼容退出见
  [语义边界](../../docs/architecture/observation-semantics.md)与[兼容台账](../../docs/architecture/compatibility-debt.md)。
- 原有回归未覆盖“新字段触发旧 SDK 缓存损坏恢复”的版本边界。新增测试先复现失败，再修复：未知版本停止加载；实际含 Aspect 才写 v2；ManagedProcess 自动回退同样在启动前拦住不兼容包，保留原数据、不造 Gap。
- 新 Source 采用 desktop-activity 进入原分析；Source=system 的未知 Aspect 完整读回但不参与活动分析；同 Revision 改 Aspect 返回 409。
- Dashboard 非 VRChat 的 account-location 使用真实 App 名称或通用账号位置文案，不冒充来源。

| 验证 | 结果 |
| --- | --- |
| .NET 全仓 `dotnet test Heartbeat.slnx --no-restore` | 1338 passed，0 failed / skipped |
| 追加 Aspect 部分索引后 PostgreSQL 迁移/存储/语义/Experience 回归 | 12 passed |
| EF `migrations has-pending-model-changes` | 模型与追加迁移一致 |
| Browser 构建与 protocol/缓存等完整测试 | 112 passed |
| Frontend `npm run verify`（类型、测试、构建） | 296 passed；由当前隔离测试应用 OpenAPI 用 NSwag 14.7.1 重新生成 DTO |
| 脚本静态检查 | person smoke `node --check`；cutover script Python compile 通过，协议4/目标迁移已同步 |
| C# 命名与 diff 检查 | IDE1006 verify-no-changes、git diff --check 通过 |
| Standards / Spec 并行审查 | outbox 回退保护、兼容退出门槛、smoke 协议、通用视图文案均已整改并复核 |

全仓回归中的 VRChat 历史样本原先复制新 Fact 后仅删除 Observer/Target，现同步删除旧格式不存在的 Aspect；
旧 identityKey→activityKey 重放验证通过。真实业务库、实际设备、部署与暂停的资源/恢复演练未执行；
这些外部门禁继续由[存储 PRD](../observation-storage/PRD.md)的 ready-for-human 状态承接，不属于本实现 PRD 的完成声明。

Friction closeout：补齐缓存版本/回退的明确边界、旧契约退出盘点负责人和验证入口，更新受触达的 glossary/ADR/迁移脚本与客户端契约。
