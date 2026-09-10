# 03 — VRChat 账号观测与事实归属

**What to build:** VRChat 活动明确属于被观察的服务账号，运行 Collector 的服务器不成为 Target。用户可以按账号回看事实；采集业务只决定活动连续性，事实身份与快照机械处理由发布层承担。

**Blocked by:** [01 — System 观测身份与设备归属完整链路](01-system-observer-target.md).

Status: ready-for-human

**Parent:** [观测身份与事实归属改造](../PRD.md)

## 实施约束

VRChat 的 FOI 与 Target 均为服务账号，用服务与实际观测到的服务内账号标识辨认。显示名、Collector 安装身份及采集宿主不能替代账号身份。具体 Collector 是 Observer，账号切换不等于 Collector 身份改变。

复用任务 01 的新事实契约。将世界/实例连续性判断与 FactId、Revision、切片及快照处理分开，只提取实际需要的机制，不扩展成完整跨语言 SDK。保留已部署事实身份与现有活动语义。

服务与 App 产品的关联用于展示或筛选，不伪造机器平台 appIdentityKey。账号在线不证明用户使用了 Quest 或任何具体设备。历史账号缺少服务身份时保留明确的历史未知状态，不把当前登录账号覆盖到全部历史。

## Acceptance criteria

- [x] 从采集会话取得真实服务账号标识并传到 Fact Target；同一账号不因显示名、Collector 重启或宿主差异改变身份。
- [x] 同一账号可被不同 Observer 独立观察，事实不会因 Target 相同而合并；切换账号后新事实指向实际新账号。
- [x] 相同世界持续、切换世界/实例及恢复保持原有活动规则，发布层负责事实修订与快照机械处理。
- [x] 活动经 Runtime、Analytics HTTP 和查询后可按账号回看，Collector 离线仍可查询；宿主设备不会被当作活动归属设备。
- [x] VRChat 的产品展示/筛选使用明确服务关联，未推断设备使用或将 VRChat 时长计入 System Report。
- [x] 旧检查点与待发快照升级、重放不丢事实、不重复；历史有据可映射，无据部分保留未知且可查询。
- [ ] 可控会话集成测试和真实账号验证分别记录结果；任何确实未完成的真实资源门禁如实标明。
- [x] 记录本批兼容入口与退出条件，完成相关回归、构建、review 与提交（基线已有测试波动见下）。

## Validation

复用 VRChatManagedCollectorTests 与 PresenceStateMachineTests，以可控会话验证服务身份传播、持续/切换/恢复。通过实际发布入口到公开查询验证账号归属和 Owner 隔离，以旧检查点验证升级身份连续性。

真实账号 smoke 验证拿到的账号标识与查询归属一致，保持凭据不进入日志或事实。自动验证通过不能被描述成已经完成真实账号验收。

## Comments

2026-09-10：依赖公共链路；无需等待 Browser 实施完成。


2026-09-10 实施记录：实现与可控会话自动验证完成，真实账号 gate 未完成，保持 ready-for-human。
具体表结构、唯一键、引用保护、服务/App 关联、字段映射和兼容退出见
[VRChat 实施记录](../../../docs/architecture/vrchat-account-observation.md)。

### 自动验证

- `dotnet build Heartbeat.slnx --no-restore`：通过，0 warnings / 0 errors。
- `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`：通过。
- 初轮 `dotnet test Heartbeat.slnx --no-restore`：1,300 项通过；review 修复新增 5 项。
- 最终 `dotnet test Heartbeat.slnx --no-build --no-restore -m:1`：1,303 / 1,305 通过，
  两项既有 macOS 退出测试超时，见下方基线对照；Server 554、Hub 315、VRChat 28 项全部通过。
  退出测试所在类单独复跑 8 / 8 通过；不将此复跑描述为最终全套全绿。
- `frontend`：类型检查通过，290 项测试通过；Browser：111 项测试通过。
- TDD 首先观察 account Target 被 HTTP 拒绝；账号切换新增契约尚不可编译；尾随换行账号错误被接受。
  逐项实施后回归转绿；用固定失败证据验证测试可捕获对应缺口。
- VRChat 独立 Managed 进程、可控会话授权、Runtime 重启、HTTP 摄入、账号/App 查询与旧 v5 缓存重放通过。
  相同 Target 不合并两个 Observer 的事实；停止 Collector 后仍可读历史；新原生活动不产生宿主设备或平台身份。
- PostgreSQL 真实 migration fixture 验证 Segment/Event 表 OID、行身份、Revision、时间、Payload 保持，
  未知历史回填、重复迁移、旧/新形状重放收敛；账号唯一性、Owner 隔离、删除/篡改/悬空引用拒绝及产品合并重放通过。
- 既有持续/切片/恢复规则通过；v2 checkpoint 保留原始备份与待发事实，旧账号未绑定当前登录。

### 真实账号与剩余门禁

未完成真实 VRChat 账号验收。本地旧 headless 实例与 v2 checkpoint 存在，但秘密目录只有加密密钥，
没有可复用的会话凭据。本次未将明文凭据输出到日志或事实，也没有向日常数据库或生产部署写入测试事实。
下一承接者为 Owner：在现有 Hub 本地授权页完成授权，按实施记录核对真实账号 ID、世界/实例变化、
停止后历史、重启及账号切换。可控会话测试不能替代这项 gate。
01 的真实 System/Windows 门禁继续保留；02 已完成，不由本批重开；05 承接生产副本演练与兼容清理。

### Review / friction closeout

固定审查点 `ff55236532f53ccb7bc80881b84f42ed046eacf7`，使用本任务暂存 diff，
与用户并行改动隔离；Standards/Spec 两轴由独立子 Agent 执行并复审。

#### Standards

初审 1 项 P2：通用 Runtime 新增具名 VRChat 分支违反 ADR-049。现已改为通用 Segment 兼容规范化，
保留既有 Browser 归属 adapter；复审 0 项可操作违规。
保留 1 项非阻塞维护性判断：多个 EF 查询重复 Target 分派；本批保持显式 SQL 可翻译表达式，
不为本次需求再引入未必要的通用查询抽象。

#### Spec

初审 2 项问题已修复并复审：旧 segments HTTP 导入未立即建立账号归属（P1）；
平台纠错可能移走仍被服务引用的产品图标/别名，并可能隐式重命名该产品（P2，同一边界问题）。
新增 HTTP 首次导入/重试/原生接管/迟到旧输入和 Catalog/Override 回归均先失败后通过。
复审 0 项剩余问题，无范围扩张。最终两个原生 Payload fixture 的调整也经 Spec 补审确认。

两轴结论：Standards 0 项遗留违规、1 项非阻塞判断；Spec 0 项遗留问题。
Conventional Commit：`feat(vrchat): attribute observations to service accounts`。

#### Friction
已明确 checkpoint、Runtime JSON、旧协议/outbox 与历史账号的兼容对象及退出门槛；
实际服务 Source 为 `vrchat.account`，服务键为 `vrchat`，在文档与实现中分别记录，避免混为同一身份。
工作开始时 HEAD 为 985acaa，并行部署提交 ff55236 已独立完成；本任务不纳入用户原有未提交模型文档与 04/05 草稿。

检查点解析增加 JsonNode 后，非法 envelope 曾绕过原有隔离路径；两条最小测试先复现再修复。
原生保管/迟到 ACK 测试的两个 fixture 改用 activityKey 并保留未知 extra 字段及完整 Payload 比较；
旧 identityKey 的兼容由 checkpoint、Runtime 和 HTTP 重放覆盖。
最终串行全套复现两个 macOS 退出测试超时：
`ApplicationExit_WithRealHostAndOfflineDurableTail_Completes`（10 秒）与
`ApplicationExit_WhenSystemIngressCannotBeSaved_ExitsOnceWithUnknownEvidence`（20 秒）。
外层 Dispose 的 `The UI loop ended before desktop shutdown completed` 覆盖了等待失败。
相同类单独复跑 8 / 8 通过；从 `ff55236` git archive 的隔离基线（没有本批实现）运行
`dotnet test collection/desktop/Heartbeat.Desktop.Mac.Tests --filter FullyQualifiedName~MacAgentHostExtensionsTests`
同样复现后者 20 秒超时（7 / 8 通过）；同基线整个 Mac 程序集又复现前者 10 秒超时（81 / 82 通过）。
两项均为本批之前已有的波动，未改动桌面退出代码或放宽测试。
影响：最终全套尚不能声明全绿；账号相关验收通过。下一步应独立定位 Host.StopAsync 的退出等待阶段，
先暴露原始超时而非 Dispose 覆盖异常，建立稳定复现后修复；本批不扩大到桌面退出事务重构。
本地证据：`/tmp/heartbeat-ticket03-serial-dotnet.log`、`/tmp/heartbeat-ticket03-mac-class.log`、
`/tmp/heartbeat-ticket03-mac-baseline.log`、`/tmp/heartbeat-ticket03-mac-baseline-full.log`。
