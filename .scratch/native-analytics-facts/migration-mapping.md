# Fact 分表：第一步映射

2026-09-09，实施前代码基准 `6f763c1`。第一步核对完成后，Owner 已授权替换迁移；
当前实体、摄入、查询与未部署迁移已按下文映射改造，并在独立测试库运行，未访问原始快照数据库。
[ADR-055](../../docs/adr/055-fact-storage-by-family.md) 是已确认模型的权威来源。
本文列出直接映射、需要保住的行为与待明确的责任，不扩展 Streams/Subjects 的完整字段方案。

## 出发点

每条事实保存一份完整内容，历史数据仍可查询，重试不会重复计数或覆盖新修订。
因此优先演进现有 ActivitySegments/InputEvents，保留旧行 Id；不再建立通用 Facts 和完整查询投影。
不为迁移预建别名表、特殊索引或新状态机，先用现有确定性身份规则和最小回归证明是否足够。

替换尚未部署的 NativeFactCustody、Designer 和 ModelSnapshot，直接从 AskingWindowIdentity
升级到分表；保留原 commit 中有效的原生上传与摄入能力。已经应用旧草案的实验库要从独立备份
重建，不能只替换文件后沿用旧迁移历史。固定旧基线之前的已部署 migration 保持不变。

## 完整旧字段映射

下表覆盖 `.local/verification/fact-family-production-snapshot/before-schema.json` 中两张旧表
的全部列。该证据采集于 2026-09-09 09:56（Asia/Shanghai），本轮只读文件，未重新查询数据库。

| ActivitySegments 原字段 | Segments 中的去向 |
| --- | --- |
| Id | 保留 Id；导入流内初始 FactId 也取旧 Id，不声称恢复了 Collector 身份 |
| DeviceId | 经 Stream → Subject 关联；Machine Subject 保留原 Device 关系 |
| Source | 保留 Source，必须与 Stream.Source 一致 |
| IdentityKey | Payload.activityKey，原字符串不变 |
| AppId | 删除冗余关联；查询经 AppIdentity → App |
| Title | Payload.title，保留值与 null |
| StartTime | 保留字段名、timestamptz 类型与值 |
| EndTime | 保留字段名、timestamptz 类型与值 |
| Attributes | 合入唯一一份 Payload，转换规则见下文 |
| AppIdentityId | 保留既有 FK |

| InputEvents 原字段 | Events 中的去向 |
| --- | --- |
| Id | 保留 Id；导入 FactId = 旧 Id |
| DeviceId | 经 Stream → Subject 关联，OwnerId 来自原 Device.OwnerId |
| EventType | Payload.eventType：1 → keyDown、2 → mouseButton、3 → mouseScroll |
| Code | Payload.code，保留数值，不转换键码 |
| Timestamp | 保留字段名、timestamptz 类型与值 |
| CodeSet | Payload.codeSet，保留原编码体系 |

两表补入 OwnerId、StreamId、FactId、Revision（初始 1）和 Payload。
Events 新增 Source，旧输入记录取 system；新增可选 AppIdentityId，历史输入无应用证据，取 null。
Segments 的 OwnerId 同样来自旧 Device.OwnerId。最终字段集合恰好等于 ADR-055 的 10 列 / 9 列。

删除旧 AppId 前复核没有 AppId 非空而 AppIdentityId 为空的行；异常应报告，不能丢关联。
既有取证确认的 38 条差异是旧冗余 AppId 残留，当前消费者已经优先使用 AppIdentity.AppId，
详见 [issue 01](issues/01-native-fact-migration.md)。比较迁移前后的实际应用归属，不要求冗余旧值相等。

## Payload 只做必要转换

- 普通 Attributes：构造 `{ activityKey, title, attributes: 原 Attributes }`，完整保留 URL、
  未知成员、数组和嵌套值。SQL NULL 映射到 JSON null，不承诺恢复两者的物理表示差别。
- 旧完整包装：沿用当前 LegacySegmentPayload 的识别边界——object 顶层 identityKey/title
  与旧列匹配，且 attributes 为 object。保留其他顶层成员，仅将已知 identityKey 改为 activityKey。
  不符合识别条件的内容作为普通 Attributes 保存，不按一个碰巧同名的键解包。
- 如 activityKey 已存在且与待迁入值不同，停止并报告冲突，不覆盖。相同值可以合并。
- 当前生产者和 Runtime 快照仍有 identityKey。新写入、旧缓存和重放必须使用相同的内容转换，
  转换后再比较同 Revision；不改写上游已保管的快照，不改 FactId，不递归改写未知 JSON。
  这只适用于活动词汇，普通 Segment/Event 的内容仍完整保存。

迁移验收比较按上述规则转换后的语义值，不用“永久复制旧整行”代替核对。

## 身份衔接：复用已经存在的证据

现有 Hub projector 与服务端代码已经给出确定性关系：

- 旧 Segment Id = ProjectedSegmentId(StreamId, FactId)。旧值无法反推出完整原生身份，但收到
  原生重放时可以重新计算并核对，不需要从标题、URL 或活动时间猜测。
- 旧 InputEvent Id = FactId。由于旧事件没有 Stream 信息，原生流之间仍须按各自身份分开。
- 旧 Machine 归属通过同 Owner 下的原 Device 核对，硬件 UUID 大小写不能拆成两台设备。
  历史 `subject:account:<uuid>` / `subject:person:<uuid>` 提取真正 Subject，不伪装成机器。
- 保留当前确定性导入 Stream 的规则与流级导入来源区别，不假造旧 CollectorInstanceId；
  不能因为 Revision 统一从 1 开始，就拿 Revision 区分历史与原生。

实现必须同时满足下面四种次序；具体查找方式通过最小用例确定，不在本步预建存储辅助结构：

| 次序 | 必须保持的行为 |
| --- | --- |
| 历史 → 原生 | 只接管匹配 Owner/Subject/Source/家族的导入行，保留数据库 Id，不增加第二条事实 |
| 原生 → 旧缓存 | 即使数据库中原先没有历史行，迟到缓存也不能重复创建或覆盖原生纠正 |
| 原生 → 原生 | 低 Revision 忽略、同版本同内容幂等、不同内容冲突、高版本合法更新 |
| 旧缓存 → 旧缓存 | 保留旧导入行为，重复上传不增行，不把旧协议的快照增长规则用于原生修订 |

首次接管是从合成导入身份切到真实原生身份；导入 Revision 不能拦住首次原生 Revision=1。
切换之后只比较原生 Revision。另一个原生流不能被当作导入行接管；跨 Owner 同身份、跨 Stream
同 FactId 仍须独立。若旧缓存缺失 Stream 导致对应关系无法唯一判定，应报告，不猜测合并。
当前严格 Event 接管还检查数据库精度下的时间及输入内容，这个边界不能在删字段时悄悄放宽。

## 两处责任边界

**微秒时间。** 写入和内容比较使用同一种数据库精度规则，重试不能因微秒截断冲突。
保留已有时间值；新增回归覆盖跨 DbContext、微秒以下差异和 2000 年前日期。
此决定只覆盖核心 Segment/Event，不能顺手套到现有 1 tick Gap 并把它截成零长度。

**IsFinal。** ADR-055 不保存 IsFinal；旧 FactStore 却靠它跨请求检查终态不可重开。
建议由已经持久保管终态的 Collection/Runtime 继续负责，Analytics 校验并比较它实际存储的
时间与 Payload，不额外建立终态存储。协议上的 IsFinal 与基本合法性检查可以保留。
Owner 授权继续后已按此责任划分实施，并修订 ADR-041/054/055；未把 IsFinal 放进 Payload 或旁表。

## 下一步的最小验证

- [x] 用真正旧 PostgreSQL fixture 验证上表全字段映射、无孤立引用，最终表列为 10 / 9 且没有 Facts。
- [x] 覆盖普通/包装/未知/null Payload 和名称冲突；迁移与旧缓存导入产生一致内容。
- [x] 覆盖上述到达次序、首次原生 Revision=1、原生缩短后旧缓存晚到、跨 Owner/Stream、
  Account、硬件 UUID 大小写与整批回滚。先验证复用既有身份足够，再决定是否需要别的机制。
- [x] 验证普通 Segment/Event 完整保管；Report、Replay、Recap、Matcher、App 管理和输入统计
  从新模型读取仍正确，筛选与聚合在数据库执行。
- [ ] 最后在独立完整备份副本逐条核对，并测受限资源下的迁移、重启和空间峰值。

取证基线为 220,146 条活动、1,891,698 条输入、382,777,023 bytes，正式演练须重新核对副本。
不承诺原地改表没有 WAL/临时空间成本。10 分钟是总停服预算，现有 900 秒命令超时不是验收。
备份继续按 ADR-055 保留；建议回退使用升级前完整备份，退役依赖永久 LegacyRecord 的旧
Down→Up 承诺。恢复前仍需保管升级后新增事实，不能丢失已经确认的新数据。

旧缓存 adapter 和旧 Payload 名称转换服务实际未排空的客户端/Runtime 状态；部署 owner
核对全部安装、未确认缓存及离线/回滚窗口后，才能移除运行时兼容。历史事实继续保留。
固定旧基线的迁移仍服务旧备份升级；保留对应 fixture。详见
[兼容台账](../../docs/architecture/compatibility-debt.md)。

分表替换的实施与自动验证证据见 [issue 01](issues/01-native-fact-migration.md)。
真实备份完整 diff、受限资源预算、恢复与现场升级仍待完成；本文件不将自动 fixture 等同于上线验收。
