# ADR-0029：生产者声明的对象引用进入公共记录契约

## 状态：已接受；实现与自动验证完成（见[业务覆盖记录](../validation/business-coverage.md)）

## 日期：2026-10-01

对象身份、识别作用域和查询关系已由 [ADR-0030](ADR-0030-unified-object-identity.md) 修订。当前字段与规范化以[对象契约](../record-objects.md)为准。

## 背景

用户需要围绕设备、应用、VRChat 账号、世界等对象查看跨 Collector 的观测。同一条 Record 可以同时出现在多个对象视图中。仅让各页面解析协议 value 后建立对象关联，会使对象发现与查询依赖每个读取端认识全部协议。

## 决策

- 将 Record 涉及的对象引用纳入公共写入契约，由 Collector 明确提交其观测中已知的对象身份。一个 Record 可以引用多个对象，对象不取代生产来源与协议身份。
- 对象随首次被接受的 Record 中的引用直接发现，无需提前注册对象。Collector 不需要先访问后端获取对象 ID 才能开始观测；后续引用同一身份时复用已有对象。
- Hub 按公共契约接管并交付对象引用。后端验证公共结构、保存引用并提供通用对象查询，不解析具名 Collector 的业务 payload；前端负责展示。该职责划分延续 ADR-0009。
- 已观测的原生标识与跨平台、跨来源的统一身份解析分开。生产者不负责断言自己无法观测的全局同一性；身份解析可以调整，不改写当初观测到的原生标识。
- 公共引用与协议 value 不重复承担同一身份字段的权威。原生身份和名称移入公共 objects，协议 value 只保留实例、标题、状态等观测属性。
- 对象查询允许跨 Track，扩展 ADR-0004 的 Track-only 读取范围。对象关系不强制构成树，也不因进入不同视图复制 Record。
- 桌面应用以设备对象作为识别作用域。同设备跨 Collector 复用应用对象，不同设备上的同款应用分别指认；产品标识相同不证明对象相同。
- 对象入口使用最新有效观测提供的名称，按观测时间判断新旧；历史 Record 保留当时的名称。迟到上传的旧观测不覆盖较新的展示名称。
- 对象视图默认只收录明确关联该对象的 Record，不根据应用、账号或时间重叠自动扩展间接关联。只有观测明确提供关联时，相应记录才进入设备等对象视图。

这扩展 ADR-0001 的记录内核边界，但不把对人的活动解释或任意关系推理放入采集过程。索引是上述公共关联的查询实现，不能成为独立于观测声明的第二份事实权威。

## 落地结构

- 公共对象值类型与规范化放在不依赖其他项目的 Contracts；Domain 和 Hub Client 共用它。Domain 只允许这个共享契约依赖，仍不得依赖 Application、Infrastructure、Hub 或 Collector 实现。
- 统一对象表只保存 UUIDv7 与 Owner；识别地址保存在 object_bindings，当前名称保存在 object_descriptions。详见 ADR-0030。
- `records.objects` 保存生产者声明和历史名称，是对象关联事实的权威。`object_descriptions` 的最新名称与 `record_objects` 关联表是接受 Record 时原子维护的投影；不能独立写入第二套观测事实。UUID 映射持久稳定。
- 名称按有效观测时间 `observedAt ?? startedAt` 更新；同时间由 Record UUID 决胜。空名称不抹除已知名称。
- 后端提供通用对象目录、跨 Track 记录查询，以及 Track 目录、分页和 Point 计数的一致对象条件。前端路由使用 UUID。
- 对象目录返回观测中出现过的 roles，不赋予对象全局主要/次要等级。首页入口选择和摘要适用性由前端决定；未知类型保留对象入口，不套用桌面时长。
- 连续下钻累积有序的上下文对象，所有条件要求在同一 Record 中成立；条件也可使用 Record、Track、Collector、Timeline 的结构身份；返回上一层恢复当时的条件。路径只表达本次浏览，不构成永久关系。
- 首页设备入口、VRChat 账号分组和完整对象目录由前端组织；时间线与 VRChat 摘要是对象页的视图。时长汇总按明确选定的观测区间取并集，保留原始来源和记录。
- Initial migration 与 snapshot 直接更新；不兼容旧数据、旧客户端或旧 URL，不升级重写期协议版本。

具体契约见 [对象引用](../record-objects.md)、[存储模型](../recording-storage-model.md) 和 [HTTP 接口](../recording-api.md)。跨原生身份合并、设备重装关联和结果更正仍不在本次实现范围。

## 参考

- [领域语言](../../CONTEXT.md)
- [ADR-0001](ADR-0001-time-ordered-observation-tracks.md)
- [ADR-0003：设备身份](ADR-0003-device-identity-across-reinstallation.md)
- [ADR-0004：重放查询](ADR-0004-track-scoped-replay-query.md)
- [ADR-0009：交付责任](ADR-0009-delivery-belongs-to-hub.md)
