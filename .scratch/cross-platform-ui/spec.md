Status: needs-info

# 跨平台 UI 恢复：上下文交接

记录日期：2026-09-20。用户计划由另一个 Agent 恢复跨平台 UI；本文件交接已确认背景，不是已经批准的 UI 架构或完整功能规格。

## 先和用户确定的范围

用户原话：“接下来我想恢复跨平台 UI 了……我不会交给你做。但我希望做的 Agent 能知道我们刚刚讨论的相关 context。”

恢复哪些功能、目标平台与先后顺序、技术栈、安装/更新方式、UI 与 Hub/Collector 的进程关系均尚未在本轮确认。若用户的新任务仍未明确这些范围，先给出一个具体用户纵切和涉及的设计决策，请用户确认；新任务已明确的内容直接沿用。遵循根目录 AGENTS.md 的设计决策规则。

历史参考可从 `e9c0d65` 的 `collection/desktop/Heartbeat.Desktop.UI`、`Heartbeat.Desktop.Mac`、`Heartbeat.Desktop.Windows` 查看。它们不是当前代码的责任或协议权威；恢复界面不意味着恢复旧数据模型、旧交付逻辑或旧客户端兼容层。

## 用户反复强调的工作方式

- 项目由实现组合而成。同一实现应能单独测，也能连接其他真实实现一起测；项目目录不决定测试粒度。已接受的责任与边界见 [ADR-0013](../../docs/adr/ADR-0013-scenarios-compose-implementations.md)。
- 具体测试可以替换，整个套件必须稳定可信。不能用增加测试数量或一次通过代替证据，也不能用重跑成功抹掉首次失败。目前没有长期稳定性统计。
- 用户拒绝了“大而空”的稳定性审查计划，也认为先改采集定时器测试的纵切过于边缘。优先选择用户真正会走的主线：安装客户端 → 接入 Collector → 采集 → Hub → 后端 → 前端显示。
- 一次交付应把选中的用户行为连接到实际结果。后续 UI 的首个纵切可围绕用户接入/启动采集并看到结果提出，但这只是建议，具体范围需要与用户确定。

## 当前已经连接的实现

先读 [CONTEXT.md](../../CONTEXT.md)、[ADR-0009](../../docs/adr/ADR-0009-delivery-belongs-to-hub.md)、[Hub 交付契约](../../docs/hub-record-delivery.md) 和 [工程验证](../../docs/verification.md)。

当前责任是 Collector 采集、Hub 持久接管并完成后端身份注册/Track 映射/上传、后端存储、前端展示。用户描述主线时的“注册 Collector”并不代表要让 Collector 或 UI 提前向后端手动注册。

本轮新增并实际运行了：

- `scenario collector-delivery`：真实 macOS Collector 单次快照 → API 未启动时 Hub 接管 → 启动 API → 自动注册、上传、PostgreSQL 落库并排空队列。
- `scenario collector-replay`：受控的真实原生应用 → 持续 Collector → Hub/后端 → 真实 OIDC 登录 → 生产 Web 查询、泳道选择与详情。断言数据库和页面中的同一 Record ID、应用身份、Target 与时间，不以“有记录”或“出现应用名称”替代。

`Heartbeat Replay Probe` 是供 Collector 观测的临时 macOS 应用，不是未来客户端 UI，也不是 Collector 替身。原生场景复用 `ScenarioEnvironment`、`ScenarioProcess`、`ScenarioCollector` 等设施；后续按实际需要复用，不为跨平台 UI 预建通用场景框架。

真实回放仍需用户在临时 Chromium 中登录。Chromium 内将已注册的 `localhost:3000` origin 映射到本轮隔离 Web 端口，不停止现有开发服务。数据、进程、环境与凭据证据的边界见工程验证文档。

安装客户端尚未实现。当前只证明“现有可执行文件启动到 Web 显示”；没有证明安装、Windows 采集、跨平台 UI、权限切换、锁屏/休眠或长期稳定性。当前 Web 的展示设计见 [ADR-0011](../../docs/adr/ADR-0011-web-timeline-presentation-ownership.md) 和 [ADR-0012](../../docs/adr/ADR-0012-web-replay-state-and-track-queries.md)；跨平台 UI 是否承载回放尚未决定。

## 失败不能丢失

本轮真实回放曾在等待受控应用区间落库时超时；用户不确定是否切换前台应用。补充前台变化、Hub 队列与落库进度诊断后再次通过，但未定位原超时原因。该问题既不能写成“已修复”，也不能直接认定为已证明的 flaky。

继续跟踪 [单次采集超时](../collector-replay-stability/issues/01-native-interval-timeout.md)。浏览器固定等待/当前时间、采集测试真实定时器/一秒截止时间仍只是此前发现的风险线索，未完成全套稳定性审查。

## 交接时的验证快照

以下均为 2026-09-20 本地证据，不是长期保证；`.artifacts` 不随 Git 提交，换机器或工作树时须重跑。

| 命令 | 结果与证据目录（仓库根目录下） |
| --- | --- |
| `./scripts/heartbeat-dev verify changed --base HEAD` | 通过 .NET、Developer CLI、前端类型/格式/lint、Vitest 与生产构建；`.artifacts/verification/20260920T022657Z-verify-changed-b5724645dd9e4faf86e2464399bc44b6/` |
| `./scripts/heartbeat-dev quality --base HEAD` | 通过；`.artifacts/verification/20260920T022614Z-quality-gate-279040d897b24750858d159c79b0a54c/` |
| `./scripts/heartbeat-dev scenario collector-replay --keep-environment-on-failure` | 最终版本主线通过，成功后环境清理；`.artifacts/verification/20260920T022417Z-scenario-collector-replay-4b64e18d917740d6b21e98962e88aee4/` |

以上 `HEAD` 在验证时为 `d088c207f563096a0d9cddf9179d25bda66c741d`；后续工作应按自己的改动重新选择比较基点。既有 fixture 浏览器用例未在本轮全量重跑，真实主线与 fixture 覆盖范围应分开陈述。

交接的目标是让新的 UI 进入这条可检查的主线。完成恢复任务后，把已接受的新责任决策和操作说明分别移入 ADR/契约文档，再删除本目录与 AGENTS.md 中的临时入口。
