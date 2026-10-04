# ADR-0030：全应用共用对象身份，识别和描述分别保存

## 状态：已接受

## 日期：2026-10-04

## 背景

对象可以是观测目标、采集工具、记录空间或观测本身。原有对象表把身份、原生标识和最新名称放在一起，业务类型又各自分配 ID。这使没有原生标识的对象难以接入，也让同一个应用产品标识在不同设备上错误地指向同一个对象。

统一身份只需回答“引用的是哪个对象、数据属于谁”。如何识别它、它有哪些属性、它在观测中扮演什么角色，应分别表达。

## 决策

- `objects(id, owner_id)` 是全应用对象身份的权威。ID 使用 UUIDv7，不要求名称、原生标识或类型字段。UUID 时间部分不表示观测时间。
- Timeline、Collector、Track、Record、Hub 的业务表共用对象主键，以外键引用 `objects.id`。业务字段留在各自表中。设备、应用、账号和世界通过同一身份表被指认。
- `RecordingObject.Create` 为新对象分配身份；`Register` 接纳生产者已分配的 Record、Hub 身份。业务创建不再分配第二个 ID。Owner 来自认证，不由引用指定。
- 注册与发现分别按其稳定地址确定复用。Timeline、Collector、Track 只保存并发冲突后最终采用的对象身份。Hub 在本地持久保存 UUIDv7，后端首次有效联络时登记同一身份。
- `object_bindings` 只保存识别映射。地址是 `(owner_id, scope_id, namespace, key)`，空 scope 表示该 Owner 内的全局标识。一个对象可以有多个地址；地址只能指向一个对象。
- 需要作用域的标识明确引用另一个对象。桌面应用用设备对象作为作用域：同设备的多个 Collector 可以指认同一应用，不同设备上的相同 bundle ID 或路径分别指认应用对象。
- 公共 Record 引用可以提供已知对象 UUID、原生标识，或两者。UUID 必须已登记并属于当前 Owner；UUID 与原生标识一起提供时可补充别名。未知、无权限或已指向其他对象的标识使该条 Record 无效。
- `object_descriptions` 保存观测产生的当前名称投影。`records.objects` 保留生产者原始声明与历史名称；`record_objects` 按规范化引用序号连接到对象，避免多个别名压成一项或读到当前名称。
- 每条 Record 的身份、记录、发现、描述与引用在同一数据库事务提交。拒绝和冲突整条回滚，重试和并发发现不留下未使用的候选身份。批次仍逐条提交。
- 通用查找直接按统一身份表读取。目录分页列出核心结构对象与显式被观测引用的对象，不默认枚举每条 Record 身份。任意已登记 Record 仍可按 ID 查找、引用和查询。
- 对象条件可指向显式引用，以及该 Record 本身、所属 Track、Collector、Timeline。全部条件必须在同一条 Record 上成立；Hub 联络不构成 Record 与 Hub 的观测关系。
- 重写期同步修改生产者、消费者、Initial migration 和 snapshot，不增加兼容层。

本决策修订 ADR-0029 中“一种原生标识对应一个对象表行”“同应用产品跨设备共用对象”及仅显式原生引用参与查询的定义。公共声明与历史快照仍由生产者提供，后端不解析具名 Collector 的 value。

## 后果

统一的是身份和引用方式。各业务类型继续独立表达自己的职责与属性，通用层无需维护类型枚举或解析特定协议。当前支持多个识别地址指向一个对象；尚不支持修正已有地址、对象合并、设备重装关联或历史结果更正。这些操作可在既有身份与识别分离的结构上另行定义。

## 参考

- [对象契约](../record-objects.md)
- [存储模型](../recording-storage-model.md)
- [对象身份创建](../../src/Backend/Heartbeat.Domain/Recording/RecordingObject.cs)
- [对象发现](../../src/Backend/Heartbeat.Infrastructure/Persistence/PostgresObjectDiscovery.cs)
