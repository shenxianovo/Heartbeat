# ADR-056：直接观测对象与 Collector 运行模型

Status: Accepted（观测模型）；统一 Objects / ObjectRelations 的存储方案重新评估，尚未实现。

2026-09-10。设备、窗口、服务账号和本人分别可以是事实直接描述的对象；采集器宿主不决定
事实归属。对象用作用域内标识辨认，以明确业务关系关联，不建立统一 Subject 父子树。

用户进一步确认先让观测模型在 Collector 中运行，再按实际需求评估存储。
窗口等 FOI 可以仅在观测期间存在，用于协调并行活动；对象关闭不删除已产生的事实。
观测对象、引用及关系不与数据库实体一一对应，也不要求所有事实保存可重建的完整对象身份。
当前存储保持不变。Browser 折叠逻辑已做运行原型演练；正式采集路径先改造 System，便于真机核对。
System 将桌面活动转场规则提取为独立运行模型，再由现有服务映射为 Segment；
同一个本机桌面的前台、标题与 away 是活动读数，不需要另建对象登记。

Browser 随后将单窗口页面活动判定提取到 `window-activity.ts`，由现有 `fold.ts` 转换为 Segment。
并行窗口仍共用原有会话状态 `open[windowId]`，不为了分开观测与 Fact 职责而复制两份运行状态；
保留字段形状以恢复仍存在的 Service Worker 会话状态；完整开发 Reload 不承诺延续 FactId。
实机验证范围见
[观测模型改造](../../.scratch/observation-model-refactor/PRD.md)。

Facts 继续按 Segment、Event、Measurement 的时间语义组织。输出时保存解释与使用结果所需的
观测内容和上下文；不据此强制全量持久化运行时模型。观测语义独立于传输连接、批次、Package 与 SDK 的组织方式。
不增加逐次 Observation 结果表或通用 Facts 内容副本。

曾提出 Objects、ObjectRelations、ObservationContexts 三表方案。用户随后指出，
统一登记所有可观测对象并建立通用关系表可能过早：对象身份与关系是业务语义，并不要求它们
先成为集中登记的实体与外键。统一对象表和关系表退回待评估；先以实际查询、资料管理和复用需求
检验必要性，不能从“模型存在这个概念”直接推出建表。共享上下文本身也不要求一个统一 Objects 外键。

本决定重开 ADR-041 的“一实例一 Subject”及 ADR-055 的观测归属设计；
保留 ADR-055 的家族表、单份 Payload、时间和正常修订语义。具体目标关联结构待存储方案收敛，
不能把语义变化当作纯命名重构。Measurement 仍等待真实来源。

[字段及迁移候选](../architecture/observation-storage-design.md)保留已完成的代码核对，已暂停作为实施基线。
已上线分表迁移不能替换、旧 Browser 缺少完整窗口身份等证据仍需尊重。
System/Browser 观测模型验收后，用户授权从 Browser 提炼最小 Segment SDK；业务观测模型与 Facts
布局保持原意，协议改造及跨语言公共 SDK 仍后置。实现范围见 [SDK 讨论](../architecture/collector-sdk-design.md)。
历史决策及运行中的旧模型保留其版本语义。
