# Browser 的 Observer 与应用上下文 Target

本文件是 [Ticket 02](../../.scratch/observation-identity-targets/issues/02-browser-application-context.md)
的实施记录，接续 [System 公共链路](system-observation-targets.md)。沿用 Observations 基线与
ADR-056；不引入 DataSource，不改变窗口观测、家族时间、事实唯一性或 Report 的注意力规则。

## 业务资料与事实存储

追加 `20260910130810_BrowserApplicationContexts`，不改已部署迁移。新增表只有：

| 列 | 约束 / 含义 |
| --- | --- |
| Id | bigint identity 主键，Analytics 内部行号 |
| OwnerId | 数据主人 |
| DeviceId | 与 OwnerId 一起外键引用 Devices(OwnerId, Id)，Restrict |
| AppId | 外键引用 Apps.Id，Restrict，App 是产品 |

唯一索引 `(OwnerId, DeviceId, AppId)`；另有 AppId 索引。窗口、Profile、进程与启动次数不登记，
不同安装的 Facts 可使用同一上下文，但仍以 `(OwnerId, StreamId, FactId)` 独立保管。
Segments/Events 继续使用 01 的 `ObserverId/TargetKind/TargetId`，不复制 Payload、不新增家族表。

单个 Target 的种类/行号对不能用一个普通外键引用不同表，因此追加 PostgreSQL 引用检查触发器：
写入检查同 Owner 的 Device/ApplicationContext 存在并持有行共享锁；删除或改变被引用目标的
Owner/Id 被拒绝。上下文到 Device/App 使用普通外键。旧 null Target 允许保留；未知 kind、
半个引用、悬空及跨 Owner 引用被拒绝。随后新增账号等 kind 时须扩充这一具体资料检查。

FactStore 的 Owner 事务同时取得既有 Catalog 事务锁，再解析平台身份和上下文。Catalog、Override
与 Merge 沿用同一锁，避免上传读到一半的产品纠错；唯一索引是最终的并发保护。本轮选择简单的
目录锁串行化；没有新建通用身份注册服务。

## 离线引用与 Observer

`FactTarget.kind = application-context`，`reference` 是 JSON 二元字符串数组，例如：

```json
{"kind":"application-context","reference":"[\"3be75ba0-ae30-5292-8c28-584cd963ccaf\",\"mac:com.google.chrome\"]"}
```

设备使用已有 HardwareId（UUID 规范为小写 D 格式）；第二项是经现有规则规范化的 `mac:` / `win:`
AppIdentityKey，不是 App 显示名、产品 slug、Profile 或 Analytics 行号。Analytics 在认证 Owner 内
解析 Device，按当前 AppIdentity→App 取得产品，再解析唯一上下文。显式 Target 与同条事实的
平台应用证据不一致时拒绝。相同引用在 App 纠错后通过当前产品映射解释。

Browser Observer 直接采用已持久保存的 `browserCollectorExternalHostIdentity` 安装 UUID。
它与 Runtime CollectorInstanceId 不同：当前一个 Browser Runtime 实例可服务多个扩展安装，
所以不能把所有 Profile 的 Observer 都设成该实例 UUID。扩展更新、Chrome/Worker 重启不分配新
Observer；新 Profile 独立安装有独立 UUID。复制整个 Profile 会复制安装身份，并不等于独立安装。

Browser 的窗口模型与最小 Segment SDK 只生成活动内容/时间。delivery 保存完整归属到
`browserFactAttribution` 和各条 pending snapshot；已有绑定的后续离线活动继续携带它。
首次尚无设备依据时先保管窗口快照，初始化协议提供既有设备 UUID 后，在发送前持久补齐。
此处只在 protocol adapter 读取旧初始化 `instance.subject`，不向 Analytics 在线领取行号。
已经绑定的快照不随新连接重新绑定。窗口关闭删除运行 FOI，不删除事实。

## 历史与缓存映射

| 边界 | 本批转换 | 保留信息 |
| --- | --- | --- |
| 已部署 Browser Segment/Event | 已保存 Subject.DeviceId + Fact.AppIdentityId→App 映射到上下文；缺 App 依据时明确映射 device | 表 OID、行 Id、Stream、FactId、Revision、时间、原 Payload 与 AppIdentityId |
| 历史 Observer | 只有 native Stream 保存了有效 externalHostIdentity UUID 才恢复；legacy-import 或无该证据为 null | 不以 Runtime 实例或当前安装补造历史 Observer |
| Runtime JSON v1–v4 → v5 | 保留原备份；有安装 UUID、设备与平台应用依据时补齐 Browser 归属；旧活动 payload 的 identityKey 规范为 activityKey | 原事实/流身份、修订、家族时间、Gap、Delivered；原文件在 `.vN.bak` 可核对 |
| Browser local pendingSegments | 旧 identityKey → activityKey；保留新增 Observer/Target；缺归属时由首次有依据的绑定补齐 | 原 id、起止时间、Revision 计算依据、窗口细节、终态与队列 |
| Browser session foldState | 旧平铺 identityKey → activityKey；不重分配正在进行的 Segment id/startTime | Worker 恢复的并行窗口活动 |
| 更早 ActivitySegment 导入 | 已有平台身份时直接解析上下文；无 App 依据时 device；Observer 未知 | 继续写同一家族事实；原确定性 native takeover 不变 |

新 Browser 活动字段与消费代码使用 `activityKey`。`identityKey` 只出现在显式旧缓存/协议/投影
适配器及对应 fixture；Runtime 同 Revision 比较前和 Analytics 存储前使用同一活动字段规范化。
旧上传缺平台字段而数据库已保留 AppIdentityId 时，以已有行的证据保留回填归属。
旧、新表示重放收敛到同一事实。新原生输入仍须提供成对有效的 Observer/Target；null 输入仅服务
改造前第一方兼容入口，不作为新 Collector 发布接口。

协议 fact serialization、字节限制、persisted publish attempt、Runtime 同 Revision 比较与上传 ACK
都包含新元数据。Browser 仅按对应完整已发送快照收敛 ACK；错误 Observer/Target 不会因同 FactId/
Revision 删除队列。重试保留原 messageId 及内容，不用改 Revision 绕过冲突。

## 产品纠错与查询

Merge 与 Catalog/Override（含删除 Override 回退）调用同一 ApplicationContextService：
为受影响 AppIdentity 的事实取得目标产品在原设备上的上下文，更新 TargetId，然后移除不再被
引用的源产品上下文。目标已存在时复用；只移动部分平台身份时，其他身份的事实留在原上下文。
整个操作与原产品维护处于同一事务，preview 回滚；只维护关联，不改 FactId、Revision、时间、
Observer 或 Payload。原 AppIdentityId 留作平台证据，支持以后再次拆分纠错。

通用 Segment/Event API 支持 `deviceId` 与 `appId` 联合过滤，返回 DeviceId/AppId 维度投影。
活动、经历、应用列表与输入查询也从有效 Target 解析设备；应用上下文直接关联产品。
Dashboard 以应用上下文作为独立 Target 泳道，Browser/System 相关观察按设备与 App 产品及时间
重叠解释，不能因两者 Target 不同就丢失页面细节。Report 仍仅统计明确 System 活动。

## 兼容退出与验收边界

移除由 05 负责，门槛为三种 Collector 新发布迁移、实际旧版本退出、持久缓存盘点和升级/重放
证据齐备、可映射历史回填、未知历史直接可查，以及明确的离线/回滚窗口。具体保留位置：
Browser storage/session 恢复、protocol 初始化 Subject adapter、Runtime v1–v4 reader 与旧信封补齐、
FactStore 无字段输入/LegacyImport、旧投影 identityKey 读取、未知历史 Subject 查询回退。
这些转换只服务改造前第一方版本和既存数据；不得只删 nullable 就宣布清理完成。

02 已解决上下文唯一性、引用完整性、设备/App 查询及旧快照重放；05 承接全量生产副本演练与
清理，不承接本批正确性缺口。Down 明确拒绝有损删除，恢复使用升级前备份，沿用 ADR-055
最多 10 分钟停写窗口与备份约定。本任务没有部署生产或迁移已有本地日常数据。

自动测试、实际 Chrome/macOS Desktop→独立 Analytics 的证据与尚未覆盖的平台见 Ticket 02。
01 的真实 System/Windows 门禁继续保持 ready-for-human，不由本次 Browser 验收关闭。

## 可复现的本地 Browser 验收

在仓库根目录执行，要求 macOS、Google Chrome、Docker、Node（支持内置 WebSocket）、.NET SDK，
以及已配置的 `.env.local` Auth 设置和 `.local/desktop/config.json` 开发 API Key：

```sh
dotnet build server/Heartbeat.Server --no-restore
node scripts/browser-development.mjs --prepare-only --profile .local/observation-browser-02/desktop
node scripts/smoke-browser-observation-targets.mjs
```

prepare 沿用本地开发套件构建 Desktop 和独立扩展，保留这个专用 Profile 的扩展身份与绑定。
smoke 创建独立 Chrome 用户目录和临时 PostgreSQL 18 容器，启动当前 Analytics 与真实 macOS
Desktop；仅借用现有开发凭据换取认证，在临时库内为该 Owner 建私有查询 fixture。全部事实写入
这个临时 Analytics，不接触日常数据库；不输出凭据。专用 Profile 配置关闭 System 窗口/输入采集。
脚本退出清理所启动的进程和容器，运行日志、缓存与报告留在 `.local/observation-browser-02/`。

成功时返回 0，并在 `smoke-report.json` 记录：真实 MV3 扩展使用两个 Chrome windows 产生不同
FactId、共享应用上下文、设备/App 联合查询命中；关闭对应窗口后其历史仍在；Desktop 停止期间
产生的完整待发快照，在 Chrome 和 Desktop 都重启后以同 FactId/Observer/Target 到达 Analytics。
这是实际 Chrome headless 进程和原生 Desktop 的链路验证；不是人工可见窗口/OS 前台注意力验证。
本次没有真实多 Profile 或 Windows/Edge 验收，也未单独验证重启后新活动；相应身份/引用规则由
自动测试覆盖。脱敏结果保存在 [验收报告](../../.scratch/observation-identity-targets/browser-smoke-report.json)。

## Review 与 friction closeout

固定基线为 `a572c3599988f97096722b465a26173e74d1df40`，审查范围只包含本任务暂存改动，
没有纳入用户原有的 ADR/模型文档和后续票据草稿。Standards、Spec 独立并行 review 后补审了
真实 smoke 脚本与最后的测试样本修正。

### Standards

0 项规范违反；1 项非阻塞 P3 判断：不同 EF 查询重复解析有效 Target 的设备/App 维度。
本批保留显式可翻译表达式；条件实体投影曾在真实 PostgreSQL 的 Report 分组查询失败，已改为
明确 left joins 并通过相关回归。此维护性建议不构成正确性欠账，也不为消除重复引入额外抽象。

### Spec

0 项确认缺陷，未发现范围扩张。复核覆盖唯一性、Owner 隔离、离线解析、历史回填、同 Revision
旧/新重放、设备/App 查询、产品合并与部分身份纠错、System Report 边界。结论不扩大实测平台。

本次 friction 收尾：补齐可判定成功/失败的真实 Browser 入口；同步消费者文档与兼容退出清单；
修正原先把新 `activityKey` 当非法形状的旧测试 fixture，继续验证未知形状拒绝；明确 Ticket 01
和 05 的剩余门禁。旧模型记录保留为历史，当前 Browser 实施以本文件及 Ticket 02 为准。
