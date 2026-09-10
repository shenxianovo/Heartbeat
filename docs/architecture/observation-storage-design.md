# 观测存储候选：统一对象登记方案已暂停

2026-09-10 后续反馈：用户保留观测模型，但质疑所有可观测对象统一登记到 Objects、再建立通用关系表是否过早。
据此，本文的三表方案及字段、索引、约束退回待评估，**不再作为代码改造或迁移的实施基线**。
用户随后明确先运行观测模型，再考虑存储演进；短暂窗口 FOI 可仅用于 Collector 并行活动管理。
本阶段保持现有存储，既不建立统一对象表，也不先替换 StreamId 或强制增加观测字段。
对象引用与业务关系的语义保留；必须用具体的保存、查询或管理需求证明独立对象表/关系表的必要性。
共享观测上下文不必引用一张统一 Objects 表。此处保留方案与代码核对证据，避免将已做的设计工作误当成必须实施的需求。

下文为本轮反馈前的候选方案。
当前未修改实体、迁移、数据库或协议，也未执行数据库演练。
模型取舍见 [ADR-056](../adr/056-observation-objects-and-contexts.md)，讨论过程见
[观测模型](collector-observation-model-proposal.md)。已核对的旧数据限制与已部署迁移基线仍有效，但不因此固定目标表结构。

## 历史候选基线（已暂停）

- 保留 Segment/Event 的家族布局、时间、FactId、Revision、Source、AppIdentityId 和唯一 Payload。
- 使用 Objects、ObjectRelations、ObservationContexts；事实的 StreamId 改为 ObservationContextId。
- **迁移时旧 Stream 与新上下文一一对应，Id 原值保留，不拆流、不合流、不重编号历史 Fact。**
- 历史 Browser 保留机器归属和原有 windowId，不补造窗口生命周期。新观测才使用可辨认的窗口引用。
- 追加一份迁移；不修改已上线的 `20260908141403_NativeFactCustody`。

核对代码基线为 `3d512d0`。仓库[原生 Facts PRD](../../.scratch/native-analytics-facts/PRD.md)
已记录 `c2fb3a2` 对应的分表部署完成；旧迁移文件内的 Unreleased 注释不再代表发布状态。
本轮仅核对仓库与官方 API 文档，没有连接生产或本地业务数据库；实施演练应重新确认实际迁移历史。

## 字段

### Objects：8 列

| 字段 | PostgreSQL 类型 / 可空 | 用途 |
| --- | --- | --- |
| OwnerId | text，必填 | 数据主人 |
| Id | uuid，必填 | 内部关联身份；不要求等于外部账号 ID |
| Scope | text，必填 | 标识作用域 |
| Key | text，必填 | 作用域内的对象标识 |
| Kind | text，必填 | device、window、account、person 等业务类别 |
| DisplayName | text，可空 | 展示名称；不参与对象判等 |
| DeviceId | bigint，可空 | 仅实际设备对象关联 Devices；窗口不在此列借用宿主设备 |
| AppId | bigint，可空 | 明确的产品归属，主要用于服务账号 |

主键 `(OwnerId, Id)`；唯一键 `(OwnerId, Scope, Key)`。
DeviceId 的非空值在同一 Owner 下唯一，保证同一设备资料只有一个规范设备对象；
外键使用 `(OwnerId, DeviceId) → Devices(OwnerId, Id)`，Devices 补相应唯一键。
AppId 引用现有全局 Apps；不按 Owner 复制产品目录。

Scope/Key 在所属标识体系下比较，不统一小写所有外部账号标识。
UUID 标识可以使用规范字符串，显示名变化不改变引用。需要更改对象身份时另建对象，
不会把已保存事实直接描述的对象静默改成另一个人、账号或窗口。
不增加通用 Attributes、ParentId、版本、哈希或凭据字段。

### ObjectRelations：8 列

| 字段 | 类型 / 可空 | 用途 |
| --- | --- | --- |
| OwnerId | text，必填 | 数据主人 |
| Id | uuid，必填 | 这项关系记录的身份 |
| FromObjectId | uuid，必填 | 关系起点 |
| Relation | text，必填 | 明确关系名称，如 runs-on、used-by |
| ToObjectId | uuid，必填 | 关系终点 |
| ValidFrom | timestamptz，可空 | 已知的有效起点 |
| ValidTo | timestamptz，可空 | 已知的有效终点，按半开区间理解 |
| Evidence | jsonb，必填 | 依据，如采集者声明或本人明确绑定；不保存凭据 |

主键 `(OwnerId, Id)`；两个端点都通过包含 OwnerId 的外键关联 Objects。
双端点时间均存在时要求 `ValidTo > ValidFrom`。
关系名称不是可随意传递的 parent，也不增加环检测、推理、路径闭包或关系版本体系。

结构关系如窗口 runs-on 设备，适用于该窗口身份的有效生命周期；无需猜测窗口创建时间。
used-by 的有效时间缺失则表示没有相应历史证明，不能推断该账号所有历史数据都属于此人。
人员使用设备的一次活动继续表达为 Segment，不在这里复制一份活动历史。
改变现实关联时结束原关联并另建关系；修正错误资料按明确依据纠正，不预建关系修订档案。

### ObservationContexts：6 列

| 字段 | 类型 / 可空 | 用途 |
| --- | --- | --- |
| OwnerId | text，必填 | 数据主人 |
| Id | uuid，必填 | 事实所引用的稳定上下文身份 |
| ObjectId | uuid，必填 | 直接观测对象 |
| Observation | text，必填 | 明确的业务内容名称，如 browser.selected-page |
| CollectorInstanceId | uuid，可空 | 具体观测者；历史没有的信息不补造 |
| Provenance | jsonb，必填 | 必要来源说明，例如 API、来源账号引用；迁移时也保留旧元数据 |

主键 `(OwnerId, Id)`；对象外键包含 OwnerId。
CollectorInstanceId 沿用当前逻辑标识，服务端没有对应的 Collector Instances 实体表，故不虚构外键。
新 Collector 生成的上下文须有实际观测者；列可空用于保留历史事实。

引用后的对象、观测内容、观测者和影响解释的来源保持原含义，切换对象或来源账号另用上下文。
不为上下文引入 Revision、内容摘要、Schema、OutputId 或 FactKind 列；家族由事实表确定。
不按整份 Provenance 建唯一键或去重哈希；不同上下文允许有相同业务内容，不能据此合并 Fact。
Source 继续位于事实表，不新增 Context.Source。

Provenance 只保存共享来源资料，逐条 URL、标题、数值等留在家族 Payload。
原生来源账号引用可表达为 `account: { scope, key }`，Owner 来自上下文；不需要专用 ReportingAccount 列。
引用本人的上下文自身固定 ObjectId，因此读取历史步数无需重新使用“账号当前使用者”推断本人。

### 家族表与缺口记录

Segments 仍为 10 列、Events 仍为 9 列，只将 StreamId 关联改为 ObservationContextId。
两表主键 Id 保留；唯一键改为 `(OwnerId, ObservationContextId, FactId)`，外键包含 OwnerId。
Source 的现有长度与基本 Revision/时间/JSON 约束沿用，不借本次重做核心校验。

现有 FactGaps 也引用 Streams，不能遗漏：StreamId 同步改为 ObservationContextId，
GapId、Start、End、Reason、EstimatedFactsLost 原值保留，包括现有 bigint 时间精度。
这只是保住现有缺口的归属，不决定未来 SDK 的传输缺口如何覆盖多个观测上下文。
Measurement 继续不建表。

### 查询需要的索引

保留两张家族表现有的 Source/时间、FactId 和 AppIdentityId 索引；重命名关联列对应的索引。
新增 `(OwnerId, ObservationContextId, StartTime)` / `(OwnerId, ObservationContextId, Timestamp)`
支撑按具体对象的上下文查询时间段，避免仅靠带 FactId 的唯一索引扫描整个对象历史。
ObservationContexts 使用 `(OwnerId, ObjectId, Observation)`；
关系使用 `(OwnerId, FromObjectId, Relation)` 和 `(OwnerId, ToObjectId, Relation)`。
Objects.AppId 使用普通索引，供产品合并重绑；Owner/Device 的唯一索引服务设备查找。
不预建 JSON GIN、通用全文、递归关系或全部组合索引；实际查询计划是进一步调整的依据。

## App 与 Device 的具体处理

设备对象的 AppId 为空。System 每条 Fact 的 AppIdentityId 继续表达当时的平台应用。
窗口产品优先由事实保留的平台 AppIdentity 解释，窗口 AppId 默认留空，避免再维护一份
会与 App Catalog override 漂移的产品映射；需要窗口产品信息时由对应观测的已知平台身份解析。
账号没有桌面平台身份时可直接关联其明确的服务 AppId，不伪造 win/mac/sys 标识。

历史 AppId 初始为空：不能只因 Payload.appDisplayName 是 VRChat 就按名字归并。
后续真实账号身份及所属服务明确后，通过正常产品解析建立关联。
所有原 Fact.AppIdentityId 保留原值，不重新按显示名解析。

`AppMergeService.Apply` 当前会重绑 AppIdentity 后删除源 App；新 Objects.AppId 必须一同纳入
计划、dry-run 和事务重绑，否则新外键会阻止产品合并。App Catalog override 的平台身份语义保持原样。
按 App 查询须区分“活动涉及的产品”和“上游数据提供方”；微信步数不能混入微信前台时长。

设备筛选使用两条有界路径：直接设备对象的 DeviceId；或窗口的明确 runs-on 关系终点的 DeviceId。
不遍历任意关系树，也不把 Collector 的服务器作为设备归因。
关系筛选使用 EXISTS 或先去重的对象/上下文集合，不能因一对多关系连接重复计算 Fact。
同一个窗口身份的 runs-on 目标由运行环境明确给出；冲突的设备依据不任选一条用于归因。
Report 继续只统计具有已知设备及平台应用关联的 system 活动，不把并行窗口段累加为前台时长。

## 旧字段到新字段

### Subjects → Objects

| 旧信息 | 新位置 / 规则 |
| --- | --- |
| OwnerId | 原值保留 |
| SubjectId | 无需归并的对象沿用为 Id；有相同 DeviceId 的 Machine Subject 仅按已知设备关联归一 |
| Kind | machine → device；account/person 保留语义；未知旧类别保持明确的 legacy 解释 |
| DeviceId | 已知设备关联原值保留，核对其 Owner |
| DisplayName | 设备以 Devices.DeviceName 作为规范名称，其余沿用；原 Subject 名称仍保存在对应 Context 的旧来源资料中 |
| 新 Scope/Key | 已知设备：heartbeat.device / DeviceId 的十进制字符串；未解析账号或本人：legacy.subject.account 或 legacy.subject.person / 旧 SubjectId |
| 新 AppId | 初始 null，不猜测产品 |

同一 Owner、同一 DeviceId 的旧 Subject 已有相同物理设备依据，使用旧 SubjectId 规范字符串
按字典序最小者作为规范 Object.Id；不同 DeviceId 不按 HardwareId 相似或名称相同归并。
没有 DeviceId 的旧对象保留自己的旧身份作用域，不假造设备。
迁移临时映射覆盖每个旧 Subject；未被任何流引用的 Subject 也需有去向。
迁移核对导出记录旧 Subject 到 Object 的对应及名称选择，但不新增永久别名表。

### Streams → ObservationContexts

| 旧字段 | 新位置 / 规则 |
| --- | --- |
| OwnerId | 原值保留 |
| StreamId | Id，原 UUID 不变，**每条旧流各保留一行，不合并** |
| SubjectId | 通过上述映射得到 ObjectId |
| CollectorInstanceId | 原值保留，包括 null |
| OutputId | Provenance.legacy.stream.outputId |
| Source | Provenance.legacy.stream.source；现有各条 Fact.Source 不变 |
| FactKind | Provenance.legacy.stream.factKind；不新增上下文家族列 |
| Dimensions | Provenance.legacy.stream.dimensions，整份原 JSON 保留 |
| Origin | Provenance.legacy.stream.origin，native / legacy-import 等原值保留 |
| 旧 Subject 信息 | Provenance.legacy.subject 保留 id、kind、deviceId、displayName，供来源解释与实际旧缓存衔接 |

旧观测内容按可靠的已有信息填写：system Segment 使用 system.activity，包含原有合成状态；
system Event 使用 system.events，不猜每条事件的具体内容；机器上的 browser Segment 使用
browser.window-state，表示机器的带窗口号状态；账号级 vrchat.account Segment 使用
vrchat.account-location。无法判定的其他组合使用 legacy.segment / legacy.event，
保留原 Source、OutputId、Dimensions 与 Payload，不假造精细观测内容。

机器上的旧 Browser 上下文不是新模型里的窗口对象，展示与 API 必须如实保留这种历史粒度。
迁移不生成 Window Object 或窗口 runs-on 关系，因为既有 windowId 没有足够的生命周期身份。
ObjectRelations 初始不从“同一个 Owner”批量生成账号 used-by 关系。

### Fact 与 Gap：逐项不变量

| 信息 | 迁移行为 |
| --- | --- |
| 数据库 Id、OwnerId、FactId、Revision | 原值保留 |
| StreamId | 仅改列名为 ObservationContextId，UUID 原值保留 |
| Source、AppIdentityId、全部家族时间 | 原值保留 |
| Payload | 不重组、不添加新外壳、不补 window scope、不重命名业务成员 |
| FactGap 的 StreamId | 同样原值改名，其余 Gap 字段原值保留 |

身份映射为 `(owner, oldStream, fact) → (owner, sameContextUuid, sameFact)`，是逐项恒等映射，
因此原来分属两条 Stream 的相同 FactId 迁移后仍然不同。对象资料归一不影响这个结论。
每个家族各自保留唯一约束；同一 UUID 出现在不同 Owner 或不同家族时也不相互覆盖。

## 新数据的对象身份

Browser 新观测引用由窗口号和可辨认的作用域组成。现有 externalHostIdentity 是 Profile 身份，
不是浏览器运行周期；Activation 又会随 Service Worker 重启变化，都不能直接充当完整窗口作用域。

[Chrome windows 文档](https://developer.chrome.com/docs/extensions/reference/api/windows#property-Window-id)
仅保证窗口号在浏览器会话内唯一。[storage.session 文档](https://developer.chrome.com/docs/extensions/reference/api/storage#session)
说明扩展禁用、重载、更新或浏览器重启都会清空会话存储。因此后续 Collector 可以在 session 中
保存独立的窗口标识作用域 token，普通 Service Worker 唤醒沿用；标识上下文丢失时另起作用域。
这可能把同一物理窗口的观测分成多个可辨认范围，但不能错误合并两个真实窗口，也不能宣称该 token
是浏览器提供的永久身份。完整对象引用必须与待传 Fact 一起保留，重放不取当前会话 token。

VRChat 新对象应使用来源实际提供的账号标识。当前 `VRChatAuthenticationState` 只返回
DisplayName 与 2FA 信息，没有保存真实 userId；当前 Headless Subject 又是安装时随机生成。
旧账号因此保留 legacy 身份，不通过一次当前登录自动重绑全部历史。本人 Person 同样需要明确引用，
不从 Owner 或账号昵称自动生成历史归属。获取新账号标识属于后续 Collector 适配，不在数据库迁移中执行登录。

## 旧客户端、重放与切换

服务端存储可以先迁，现有上传 DTO 的 Stream/Subject 进入一个有明确范围的旧接口 adapter：

- 已有流用原 StreamId 查同 UUID 上下文，按 Provenance.legacy 保留原定义校验；
- 尚未出现的旧流按同一规则建立旧粒度上下文与已知对象，允许未升级客户端继续交付；
- 旧 metadata 的 native / legacy-import 区别继续用于既有导入接管，不以 Observation 名称冒充 Origin；
- `ProjectedSegmentId(oldStreamId, FactId)` 仍用原 UUID，InputEvent 原 FactId 衔接仍按既有规则；
- 保留“历史先到、原生先到、旧缓存晚到、同版本重试、旧修订晚到”的当前行为，不重写未确认 outbox。

新窗口级上下文只承接新观测；不将仍在旧上下文中的进行中 Fact 自动移入新上下文。
切换时已有事实继续保持原绑定，直到其正常结束或有明确的 Collector 升级切换方案。
这一绑定要求是持久身份衔接，不在本轮重设计连接、握手、重试和 ACK。

兼容对象是实际已安装 Desktop/Headless/Browser 及其未确认状态；退出条件是安装矩阵升级完成、
旧格式 pending 清零、离线与回滚支持范围明确，并有重放 fixture 证明不会丢失或重复。
满足后删除旧入口 adapter 和专为旧定义比较服务的代码；历史来源说明、历史粗粒度对象及已发布迁移保留。
不建立永远同时写 Subjects/Streams 和新表的双写路径。

## 实施次序与验证入口

1. 用已经包含 NativeFactCustody 的独立 PostgreSQL fixture 建立基线；追加迁移。
   建立 Objects 与临时 Subject 映射，原 Streams 原地演进为 ObservationContexts；建立空关系表。
   保存旧元数据、替换对象外键后删除旧 Subject 表；家族表和 Gap 仅做关联列/约束重命名。
   不修改已发布迁移；从更老数据库升级时先跑历史迁移，再跑本次迁移。
2. 同步 EF 实体、ModelSnapshot、摄入、旧入口 adapter、查询及 App 合并引用。
   C# 跨文件重构执行仓库 dotnet-refactoring 流程；本轮文档准备没有执行该重构。
3. 验证新对象/上下文的存储与查询能力；Collector 身份采集及 SDK 适配在后续阶段接入。
   存储完成不等于实际 Collector 已开始输出窗口对象或真实账号对象。
4. 在独立真实备份副本做逐行核对、重启和受限资源演练；最后另行执行现场升级。

| 验证主题 | 必须证明的行为 | 现有入口 |
| --- | --- | --- |
| 迁移 | 家族行数、Id、Payload、Revision、时间和 AppIdentityId 全等；旧 Context UUID 等于旧 Stream UUID；无孤立引用 | FactFamilyMigrationTests；新增追加迁移 fixture |
| 身份 | 两旧流使用同 FactId 不碰撞；同设备多个旧 Subject 归一仍不合并事实；跨 Owner 不互串 | FactStoreTests、FactHttpTests |
| 历史信息不足 | Browser windowId 重复不产生假窗口；未知账号不映射本人或真实账号；未知 Source 内容完整保留 | 新迁移场景 fixture |
| 重放 | 原生/旧缓存不同到达顺序仍只有正确一条；旧 Revision 不覆盖新；接管保留数据库 Id | FactStore.LegacyImport 相关测试 |
| 关联查询 | 新窗口经 runs-on 能按设备筛选且多条关系证据不重复计数；账号无需 Device；提供方不冒充活动产品 | AppDbContext.FactQueries、UsageService、ExperienceService、ReportService 对应测试 |
| 产品操作 | App 合并同时重绑 Objects.AppId；平台身份重分类不留下窗口的第二份旧产品映射 | AppMergeService、AppCatalog 相关测试 |
| Gap | 数量、身份、边界、原因、估计损失不变，尤其不改变 bigint 精度 | FactStore / migration fixtures |
| 恢复与资源 | 后期失败回滚，重启不重复迁移，原表不复制整份内容；停服、磁盘/WAL 预算用实际副本测量 | 既有独立快照演练流程 |

已有线上记录出现过 767.3 秒迁移，不能用之前副本的 37.4 秒代替本次性能证据。
本设计预期降低家族行改写量，但 ALTER/FK/新索引仍可能锁表或扫描；不承诺未经演练的耗时。
保留升级前备份与升级后已确认新事实的恢复安排；Down 不宣称能把新窗口粒度无损还原成旧 Subject 模型。

## 本轮完成证据

已核对：实体全部字段、EF 键与外键、原生/旧缓存身份衔接、Browser 实际落盘字段、Headless 账号创建、
VRChat 登录返回值、应用合并删除路径、家族查询和 FactGaps 归属。
身份不碰撞由保留旧 UUID 的恒等映射保证；自动数据库验证仍需在实现阶段执行，不能把文档核对称为测试通过。
本轮完成的是字段与映射方案，未写迁移代码、未运行应用测试、未访问业务数据库或部署。
