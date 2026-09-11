# Browser storage 与安装验收

2026-09-11，Observations Ticket 05。本文只描述 Browser 专有状态；通用 Runtime/SDK 矩阵见
[缓存接管](../../../docs/architecture/observation-cache-compatibility.md)。没有导出或修改 owner 的生产
Profile。下述固定 UUID、页面、时间是按历史源码定义构造的 fixture，不能冒充现场安装清单。

## 实际历史布局

旧 Chrome storage 没有统一文件 schema 数字。下表 1–4 表示源码格式代际，测试没有给当前 JSON
换一个版本号来模拟旧数据。新 atomic journal 显式写 `schemaVersion: 5`。

| 代际 / 定义依据 | local keys 与快照 | session / 已交付事实 |
| --- | --- | --- |
| 1 / `803065b` | `pendingSegments`、`browserCollectorDeadLetters`；`identityKey`、可选 `appHint/appName`、可缺 `isFinal`；Gap 可为单对象 | `foldState.open[windowId]`；policy 可在 session；无 delivered key |
| 2 / `6a0a4b0` | 同上，加持久 `browserCollectorExternalHostIdentity`；Gap 的 UUID 一次生成后持久保留 | `collectorProtocolPublishAttempt.snapshots` 保存准确发送快照；ACK 后 pending 和 attempt 可被删除 |
| 3 / `985acaa` | `browserFactAttribution` 与 snapshot `observerId/target`；`activityKey` 接替 `identityKey` | SDK fold 仍只有 Id、startTime 与窗口读数，不含已 ACK 版本高水位 |
| 4 / `6526e13` → `e6564fc` | `pendingObservationFacts`、`browserObservationDeadLetters`、`browserObservation`；直接 Collector/FOI/Relations；新旧 key 可并存 | 同上；所有旧快照按 endTime 派生版本，不能据完整 Result 是否已 ACK 进行猜测 |

JSON 在 `tests/fixtures/browser-storage/generation-{1,2,3,4}.json`。`delivery-chrome-e6564fc.ts`
是固定历史 loader（只有 import 路径调整），用于实际执行旧包读取路径。旧 payload 不含独立
delivered flag：已 ACK 进行中事实由 Runtime 保管；本票不虚构一个 Browser delivered key。

## journal、升级与回退

`browserObservationJournal` 同时保存 fold（含 `revision/lastSnapshot`）、pending、完整 dead-letter
列表、Gap、归属和 policy。正常事件、周期快照和窗口关闭通过一个 checkpoint 保存 fold/outbox；
storage 写失败时二者都保持上次持久状态。不能先删除 open 再写 final，不能先增加本地版本却没有
对应待发责任。失败的有计划 Reload 不执行 reload。

首次迁移先读取并验证旧 keys，完整备份到 `browserObservationPreJournalBackup`；备份、journal
和回退 fence 在同一次 `chrome.storage.local.set` 发布。失败前不改原 key，不制造 Gap；重试沿用
原 FactId、旧 endTime 版本和完整内容。备份保存当时的 local/session 原始布局，永不自动回灌。
未知结果字段继续保留，dead-letter 的相同 Id / 相同版本冲突条目不会经 map 覆盖。

新格式将 `pendingSegments` 和 `pendingObservationFacts` 都放置明确的不可解析 snapshot fence。
原因是历史 loader 根本没有 schema 检查；实际 `e6564fc` loader 在读队列时拒绝 null snapshot，
其写入与发布路径不会运行，测试确认存储逐项不变。新 loader 也拒绝较新的 journal schema 或
被旧 writer 另行改写的队列 key。这个证据只覆盖列明的历史读取实现；不能据此声称所有从未盘点的
历史包都会安全拒绝。运行旧包不是恢复流程，不能手工删除 fence 或把备份覆盖当前 journal。

迁移不为旧事实赋 `kind`。有准确 pending/publish-attempt/dead-letter 快照时，fold 保留原 Id，
将已有最大版本与完整 lastSnapshot 接入单调计数；转换本身不增加版本。若旧已 ACK 快照已从
扩展消失，则标记 `recoveryRequired`，保留 fold 且不离线增长、终结或改写它。通过该 Activation
实际 Stream 的授权恢复入口取得 Runtime 准确旧事实后，交付层按原完整 Result、原 endTime
收尾（新的 final 内容才加一版），继续以旧身份负责待发与 ACK，再按当前真实窗口重开新事实。
Runtime 明确找不到对应事实时保留待恢复责任，不猜测版本；运行中恢复的协议/HTTP证据由本票
主验收记录承接。

`browserObservationSessionStarted` 只区分 Service Worker 重启与整个浏览器退出。前者延续
同一 Id 和修订；后者在最后已知完整快照的 endTime 收尾，随后从当前观测重开，避免把浏览器
停机区间计入旧事实。新建但尚未 flush 的原生事实只按已知 startTime 生成零长度 final，
不延续经过停机的旧起点。停用也准确收尾已知活动，保留待恢复旧 fold；没有本地完整快照的旧 fold
仍等待 Runtime 恢复。窗口 Id 只在 Result
中提供证据，不注册持久 FOI；安装 UUID 始终是实际 Collector，Runtime Instance 是保管者。

## 自动验证与真实安装步骤

可独立运行以下测试，它们执行真实 Chrome adapter、fold/SDK、background listeners 与 delivery
checkpoint，Chrome API 使用可控 storage/event fixture。真实 Chrome Profile 的运行 fixture
与真实 Runtime/HTTP/PostgreSQL 读回由本票其余证据补充；这组测试自身不能替代实际浏览器安装。

```sh
npm --prefix collection/collectors/Heartbeat.Collector.Browser test -- --run \
  tests/browser-storage-cutover.test.ts tests/browser-background-cutover.test.ts tests/delivery-chrome.test.ts
```

覆盖四代恢复、迁移写失败与重试、原 loader 回退拒绝、完整未知字段、重复 dead-letter 证据、
原子关闭失败、两个窗口同页、EndTime 相同的标题修订、关闭重开、SW 重启和浏览器重启。

owner 在批准的真实 Chrome/Edge Profile 上承接以下安装验收，状态保持 `ready-for-human`：

1. 先记录浏览器/操作系统、精确 Package 版本与 content hash、扩展安装 UUID、Runtime Instance、
   当前旧 key/journal schema、pending/进行中/dead-letter 数量和最老时间。备份 Profile 与对应
   Runtime 数据目录；不能清空 storage 或卸载扩展来假装升级成功。
2. 按项目既有独立 Package 安装步骤加载同一扩展身份的新制品；记录 UUID 没有改变、Runtime
   接受 `facts.observation:2`，并保存新 journal 与备份存在的证据。不要在 owner 生产 Profile
   用开发制品或临时服务替代已批准部署。
3. 同时开两个窗口访问同一页面，分别改标题/转场/关闭其一/重开；核对独立 FactId、正确 windowId
   Result、同 App FOI、明确 observed-on 与扩展 Collector UUID。服务端读取必须保留两条并行事实。
4. 在受控环境暂停目标 Runtime 连接，制造待发、停止/唤醒 SW，再重启整个浏览器；恢复连接后
   核对本票规定的 Id/Revision/final、已 ACK 高水位恢复与准确 ACK。若旧事实 missing，记录原 Id
   和 Runtime/cache 证据交恢复责任人，不删旧 fold，不补造 Gap。
5. 在隔离 Profile 副本尝试明确支持的旧制品读取，确认拒绝且新 journal 原样；恢复兼容制品并
   排空。真实 owner Profile 不执行这一步回退试验。

实际安装全量 Package/contentHash 清单、最长离线时间及允许回退窗口由 owner 与原
`observation-storage` 发布门禁承接，当前没有现场证据。这些门禁没有完成前保留上述兼容和
备份消费者；只有所有受支持 Profile 的待发、进行中、隔离事实有明确归宿并跨过批准窗口，
才能以矩阵加现场证据审议删除。未执行生产迁移、部署、真实账号操作或暂停的资源演练。
