# Observations 与 Facts：当前模型基线

状态：2026-09-11，用户重新确认核心模型与五表存储目标；服务端存储及追加迁移已实现，业务库未迁移。DataSource 暂不纳入。
本文件汇总已确认的基础框架；术语见 [Shared Kernel](../../shared/CONTEXT.md)，
当前决策见 [ADR-059](../adr/059-observation-storage-five-tables.md)，
字段见[最小存储方案](observation-storage-minimal.md)。ADR-056 保留此前决策及实施背景。

## 存储的核心承诺

2026-09-11，用户确认：**多年后，仍能理解这是谁对什么对象、在什么时间、作出了什么观测。**

存储应保留足够的信息，解释 Observer、FOI、观测的 Aspect、结果及适用时间。
这一承诺作为后续存储设计的判断依据，不直接规定表结构，也不要求每个概念独立建表。
历史未保存的信息仍明确保持未知，不为满足承诺而补造观测依据。

## 两个模型

**Collector 对 FOI 产生 Fact，Fact 包含 Aspect、Result、Time。**

- Observer 是具体 Collector，不只是类型；身份跨停止、重启和更新保持。
- FOI 当前包括机器、App 产品、服务账号和个人；不要求窗口登记或应用上下文实体。
- 一个 Observer 可以观察多个 FOI，一个 FOI 也可以被多个 Observer 观察。

**Facts：Segment、Event、Measurement。**

- Aspect、Result、Time 共同表达完整事实；三个家族保留各自的时间和正常修订语义。
- Measurement 可适用于时刻或区间，不能把它一律当作点时间事实。
- 一次读取不必产生一条新 Fact，多次观测可以支撑同一条 Segment 的增长。

## 事实与对象

每条 Fact 引用具体 Collector 和一个 FOI，表达该对象的 Aspect、Result、Time。
机器、App、账号和个人可以独立存在，不以另一种对象存在为前提。
App 是跨设备产品；设备信息在有依据时通过明确关系表达，不要求创建应用上下文实体。
例如 Windows1 上的 VRChat 使用账号 A，是 device、app、account 三个角色共同参与的有时效关系。
只有账号观测时，保存账号 Fact；没有设备/App 运行证据，就不建立运行关系。
账号属于某服务不证明其运行设备，Collector 的宿主也不自动成为被观测设备。

```mermaid
flowchart LR
    O[Collector / Observer] -->|产生| F[Fact]
    F -->|描述| B[FOI]
    F --> A[Aspect / Result / Time]
    R[有时间与依据的对象关系] -->|成员角色| B
```

## 对象身份与关系

- 对象由稳定标识辨认，展示名称不参与身份判断。多个 Facts 指向同一对象不代表同一个 Fact。
- 对象关系有明确业务含义、成员角色、已确认适用范围及依据；缺少关系不阻止保存事实。
- 关系的空时间边界表示该方向无界，不能代替时间未知；不能用当前关联覆盖没有证据的历史。
- 使用者关联由 Heartbeat 在 Facts 外维护；补充或纠正关联不改写原始观测结果。
- 关系种类与角色按实际需求限定，不做任意关系推理。

## 本轮范围

目标核心表为 Collectors、Objects、Facts、Relations、RelationMembers。
DataSource 和计算口径暂不展开；Measurement 的具体数值业务约束待真实需求明确。
当前代码的 Segment/Event 已从第一方 Collector、SDK、Runtime、HTTP 贯通到五表与 Dashboard：
原生输入直接携带 Collector/FOI/Relations，查询按 Object UUID 和准确关系运行。
旧缓存和 HTTP 在入口转换；应用上下文实体与本人关联旧表已由对象/关系替代。
业务库尚未迁移，暂停的完整副本资源/恢复演练未恢复。
此前方案保留在[历史讨论](collector-observation-model-proposal.md)、ADR-055/056 及
[上一轮切换记录](observation-target-cutover.md)；与五表目标冲突的选择以 ADR-059 为准。

显式 Aspect 已从第一方 Collector 贯通到存储、分析及 Dashboard；具体契约、旧缓存升级与协议切换见
[Fact 的观测语义边界](observation-semantics.md)。
