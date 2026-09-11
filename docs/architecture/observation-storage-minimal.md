# Observations 最小存储方案

状态：2026-09-11，用户确认的目标设计；服务端存储与追加迁移已实现，业务库未迁移。
模型见 [Observations 与 Facts](observations-model.md)，取舍见 [ADR-059](../adr/059-observation-storage-five-tables.md)。

## 核心承诺

多年后，仍能理解这是谁对什么对象、在什么时间、作出了什么观测。

**Collector 对 FOI 产生 Fact，Fact 包含 Aspect、Result、Time。**
Observer 是具体 Collector，身份跨重启保持。当前 FOI 为机器、App 产品、服务账号和个人；
不要求窗口登记或应用上下文实体。对象独立存在，关系缺失不妨碍保存事实。
DataSource 暂不纳入；计算口径留到存储模型之后讨论。

## 五张核心表

以下记录用户确认的核心业务字段，不是可以直接上线的完整 DDL。
Owner 隔离、既有全局 App 目录的作用域、历史未知及身份迁移规则须在迁移设计中补齐，
不能把本表省略 OwnerId 理解为取消所有权保护。

### Collectors

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| Id | uuid，主键 | 具体观测者的稳定身份，跨重启保持 |
| Kind | text | system、browser、vrchat 等观测者类型 |
| Name | text | 可修改的展示名称，不参与身份判断 |

### Objects

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| Id | uuid，主键 | 可被引用的对象身份 |
| Kind | text | machine / app / account / person |
| Scope | text | 标识所属的命名空间或服务 |
| Key | text | 作用域内稳定标识 |
| Name | text | 可修改的展示名称，不参与身份判断 |

核心唯一约束为 `(Kind, Scope, Key)`；其与 Owner/全局产品作用域的组合在迁移设计中明确。
Mac1、Windows1、Chrome、VRChat、账号 A、本人各有自己的记录。
Chrome 安装在不同设备上不产生多个 Chrome 产品身份。

### Facts

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| Id | uuid，主键 | 由生产者稳定生成的事实身份，重传与修订保持 |
| CollectorId | uuid，外键 → Collectors | 产生事实的具体观测者 |
| FoiId | uuid，外键 → Objects | 事实主要描述的对象 |
| Kind | text | event / measurement / segment |
| Aspect | text | 描述对象的什么属性、状态或事件 |
| Result | jsonb | 结果；允许结构化内容及对象引用，仅存一份完整内容 |
| StartTime | timestamptz | 适用时刻或区间起点 |
| EndTime | timestamptz，可空 | 区间终点；为空表示时刻事实 |
| Revision | bigint | 同一事实的修订序号 |

- Event：StartTime 为发生时刻，EndTime 为空。
- Segment：两个时间字段表达当前已观测区间；不将 EndTime 为空解释为进行中的 Segment。
- Measurement：可适用于时刻或区间；Result 必须表达相应数值、单位及必要的累计等语义，
  不退化成统一的“时间戳 + 数字”。具体业务约束待真实采集需求确定。
- 最小布局是一张 Facts 表，通过 Kind 和约束区分家族，不另存完整家族投影副本。
- 本方案未授权把旧 FactId 直接作为新主键；旧身份含 Owner、Stream、家族作用域，须单独设计无碰撞映射。

### Relations

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| Id | uuid，主键 | 一项关系的身份 |
| Kind | text | 明确关系种类，如 installed-on、application-account-use |
| ValidFrom | timestamptz，可空 | 已确认适用范围的起点 |
| ValidTo | timestamptz，可空 | 已确认适用范围的终点 |
| Evidence | jsonb | 关系成立的依据，如观测事实引用或明确人工确认 |

有效范围使用半开区间；空边界表示该方向无界，不能冒充时间未知。
无证据时不建立关系；不能根据 Collector 的当前部署位置补造历史运行关系。
关系种类先限定为实际支持的几种，不做任意关系推理。

### RelationMembers

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| RelationId | uuid，外键 → Relations | 参与的关系 |
| Role | text | device、app、account 等成员角色 |
| ObjectId | uuid，外键 → Objects | 该角色对应的对象 |

主键为 `(RelationId, Role, ObjectId)`。各关系种类允许的角色、对象种类和成员数量，
在写入契约与约束设计中明确；复合主键本身不表达这些规则。

## 三元关系示例

关系 R：application-account-use，适用时间 10:00–11:00。

| RelationId | Role | ObjectId 所代表的对象 |
| --- | --- | --- |
| R | device | Windows1 |
| R | app | VRChat |
| R | account | 账号 A |

这表示这台设备上的应用在该时间使用此账号，不扩展为所有设备上的 VRChat 都使用此账号。
只有账号观测时，Facts 直接引用账号 A；没有运行证据，就不建立设备/App 使用关系。
账号属于 VRChat 服务，并不证明实际在 Windows 或 Quest 上运行。

## 实施边界

当前服务端已采用统一 Objects、单份 Facts 和关系成员。旧上传仍由 Observer/Target 转换，
原资料、产品目录与缓存身份继续保留；Target 和应用上下文不再是目标领域模型中的独立 FOI。
实际补充字段、Owner 隔离、全局产品、历史 null、关系证据外键与兼容退出条件见
[迁移映射与实施](observation-storage-migration.md)。

当前生产者及 Kind 约束支持 Segment/Event；Measurement 的具体业务约束仍待采集需求确定。
点事实的 observed-on 关系两端等于该事实时刻，只通过精确 Fact 引用参与设备归属，
不把零长度关系解释为覆盖其他事实的时间区间。

追加迁移和隔离 PostgreSQL 测试已实现；业务数据库、暂停的完整副本演练及生产部署均未执行。
