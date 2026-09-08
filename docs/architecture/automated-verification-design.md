# 主链路自动验收设计

目标：修改后自动证明关键行为正确，失败时提供足够的定位证据，减少人工启动、操作和检查。
固定回归由程序执行和断言，开发助手负责调用、分析及探索；稳定的探索场景再转为固定回归。
日常运行监控和探索性体验验收属于另外的范围。

## 当前覆盖

核对日期：2026-09-08。以下区分已实现能力与已有验收证据，不表示当前工作树已重新运行真实链路。

| 场景 | 自动断言 | 边界 |
| --- | --- | --- |
| `headless-main` | Reference ManagedProcess → Headless → Analytics，指定 Segment 及 Account Subject 映射 | 从已安装状态开始 |
| `desktop-main` | Reference ManagedProcess → 原生 Desktop → Analytics，指定 Segment、真实 Machine 映射、UI ready、安装能力关闭及退出码 0 | 独立 Profile；不验证真实窗口/输入刺激 |
| `disconnect-upload` 故障选项 | 上传目标接受 TCP 连接后立即 reset，delivery 应超时且命令非零退出 | 不覆盖长期无响应网络、恢复重传或退出后的缓存完整性 |

Reference Package 按场景生成 Account 或 Machine 版本；固定的 `reference.account` Source 不代表
Subject kind。Desktop 启动 System BuiltIn，但本场景只断言 Reference 事实，不能推定 System 采集正确。
Dashboard、真实 Browser/第三方账号、Registry 下载安装、服务内部专项检查及 CI 泳道调用尚未接入。

已有 macOS 源码和解包 `.app` 的真实链路证据；Windows/Linux 完整泳道、Setup、更新、自启动和
真实权限仍需各自验收。历史运行标识、制品 hash、回归结果及未通过项见
[2026-09-07—08 验收记录](../verification/2026-09-07-08-main-path-evidence.md)。

## 环境与所有权

验证泳道是一次验收拥有的隔离服务与数据集合；与 Dashboard Replay 中的展示泳道含义不同。
PostgreSQL 使用独立 Docker 容器，Analytics 与 Headless/Desktop 使用真实本机进程，Reference 由宿主启动。
它不复用开发栈的数据库、端口或安装状态；外部 Auth 沿用线上服务，不属于待测对象。

准备阶段复用正式 Package Installation、Runtime API 与用户供给模块建立已安装状态和 private owner，
不预置待测 Fact。输入仍需完整的 Headless 配置结构，运行只沿用身份与认证设置，凭据不进入场景或报告。

Desktop 复用正式 Host、原生 UI 与退出事务。Profile 隔离不改变真实 Machine 身份，也不授予安装版的
自启动或更新能力；目录互斥及旧版兼容边界以 [ADR-053](../adr/053-desktop-profile-and-installation-ownership.md)
为准。遵循 ADR-048/049，参考采集器不能写死进通用宿主组合。

每个服务可从源码 publish 或选择已有制品，随后执行相同场景。制品验收直接运行预构建验证器 DLL，
指定全部服务制品时不依赖服务源码可编译；验证器自身的准备步骤仍构建源码依赖。每次记录解析后的
制品入口、版本与制品树 hash；操作入口及参数统一见[验证器 README](../../tools/Heartbeat.Verification/README.md)。

## 通过与失败判定

- 准备后数据库和查询 API 中均没有待测 Segment；运行后经正式 Segment API 读到唯一匹配记录。
- 核对 Source、IdentityKey、标题、起止及 300 秒时长，再验证持久化的 owner/Subject 映射。
  端口可连、HTTP 200 或任意历史水位推进不能代替这些断言。
- 原始 FactId 与投影后的 Segment Id 不相等；当前断言不复制投影哈希算法，报告记录实际 Segment Id。
- 阶段失败、依赖阻塞、取消与清理结果分别记录；清理失败必定非零退出，不能被 delivery 成功掩盖。
  超时采用协作取消，同步文件操作要等返回后的取消检查生效；具体预算见 README。
- 默认删除临时制品、业务数据与私密配置，保留脱敏报告和日志；清理失败保留现场，`--keep` 显式保留
  至操作者请求停止。只回收本次拥有的资源。

当前证据包括运行阶段/耗时、制品身份、预期/空基线/实际查询、服务日志及独立清理结果。
这些阶段是验证器的执行阶段，尚未形成同一 Fact 在采集、Hub 接收、缓存、上传、查询各环节的关联证据；
`delivery` 超时只能证明截止期限未查到期望结果，不能自动归因到某个服务。
[Local Data Smoke](../runbooks/local-data-smoke.md) 另行检查历史数据不变量和水位，仍需人工产生刺激，
不能替代指定行为的端测。

## 实现责任

| 入口 | 责任 |
| --- | --- |
| `VerificationCommand` | 场景选择、阶段顺序、取消与最终退出码 |
| `VerificationLane` | 本次资源、制品、地址、进程启动与回收 |
| `MainPathScenario` | 正式前置准备、真实链路刺激与结果断言 |
| `RunReport` | 阶段结果、脱敏证据和独立清理结果 |

采用 .NET 以复用安装模块、Runtime、DTO 和 EF 用户供给，不复制私有状态格式或数据库 schema。
新增场景复用资源与报告，独立表达刺激和通过条件。验证器自身测试随 solution 执行，不访问线上 Auth；
真实泳道按需运行，尚未接入 CI。新增能力只有获得对应真实运行证据才算验收完成。

## 下一条端测（2026-09-08 设计中）

已确认：自动执行真实窗口操作，验证 System 采集结果进入 Analytics；失败时指出最后已确认成功的
环节，并保留同一测试事实的关联证据。证据不足明确为未知，由开发助手继续分析。
平台、窗口刺激和干扰边界、具体断言、证据接入与触发方式仍待设计确认，本节不表示实现已完成。

已知未关闭问题：历史网络黑洞运行中 Desktop 正常退出超过 30 秒；现有 TCP reset 场景没有验证该问题
已解决。后续退出验证仍需覆盖长期无响应请求与最终缓存保管，不因正常链路或进程回收通过而关闭。
