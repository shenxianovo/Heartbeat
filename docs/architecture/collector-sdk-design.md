# Collector SDK 与协议简化讨论

本文件记录 2026-09-09 起逐项确认的设计，不代表当前实现已经完成，也不作为整套协议重写的授权。

## 当前状态：Browser 最小 Segment SDK 已实现，协议改造后置

2026-09-10：观测模型保留，统一 Objects/ObjectRelations 存储方案退回待评估；
[存储候选](observation-storage-design.md)已暂停作为实施基线。后续 SDK 以直接对象及观测语义为输入，
不预设每个对象都需要统一登记；本文件旧 Subject/Stream 示例不再限制新接口。
System 与 Browser 观测模型验收后，用户已授权提炼 Browser 的最小 Segment SDK。
当前实现位于 Browser 的 `src/sdk/segments.ts`：startSegment、update、observe、end 及会话检查点恢复。
Browser 只提供活动读数和观测时间，不再生成 Id、计算起点或轮转；修订编码、Stream、ACK、重试
沿用当前 delivery/protocol 模块，未改协议或 Facts 存储。这一步尚不是独立发布的跨 Collector SDK。

验证：108 项 Browser 测试通过，本地自动 Reload 后恢复精确包连接、双窗口快照增长和已有记录上传确认。
完整开发 Reload 实测重新开始活动，不作为同一 Fact 续接的证据；保存状态的恢复由回归测试覆盖。
详情见 [最小 Segment SDK 实施记录](../../.scratch/collector-segment-sdk/PRD.md)。

以下保留此前讨论过程，其中“暂停”是当时阶段的决定；当前只推进上述最小范围。

后续已确认：直接观测对象的业务模型独立推导，用户允许先破后立，不再用旧 Subject 的类型与归属约束候选设计。下文依赖旧 Subject/Stream 的具体解释保留为讨论历史，待模型确定后重审；SDK 管理交付的目标不变。

用户要求先参考传感器领域的 Feature of Interest，厘清观测模型，再继续 SDK。
以下已讨论的开发体验保留为历史决定，但不据此推进接口、Stream 划分、协议或持久化改造。
设备活动、具体浏览器窗口已完成运行模型改造；VRChat 账号活动和本人步数继续作为后续使用场景。
已有 Facts 存储保持不动；若概念模型暴露其表达限制，明确列出差距后讨论，不静默增加字段或沿用未经确认的假设。

模型阶段按以下顺序推进：先核对标准概念，再为每个真实场景写清“哪个对象的什么属性，在什么时间得到什么结果、依据什么观测”，随后处理对象身份与关联，最后对照现有 Subject/AppIdentity/Stream/Facts 的映射与差距。
标准术语不自动成为数据库表、必填协议字段或格式注册规则。模型确定后才恢复 SDK 与协议设计。

## 已确认：作者与 SDK 的职责

目标：未来新增 Collector，作者只需关心观测对象，以及如何把观测信息表达为 Facts，不需要再次实现协议接入。

- Collector 负责与观测来源交互，并表达事实的业务内容。
- SDK 负责 FactId、Revision 的机械维护，以及连接、重试、确认等协议交付工作。
- 用户已选择由 SDK 管理事实身份与修订，而非要求 Collector 作者手工组装这两个字段。

现有模型使用 Subject 表达事实主体；它是否等同于 Collector 直接接触的观测对象，本轮仍在讨论，不能把两者直接画等号。SDK 如何取得、绑定 Subject，以及如何向作者暴露创建和更新事实的接口，尚未确定。

当前 `CollectorFact` 仍由调用方提供 FactId、Revision；上述职责是本轮确认的设计方向，不是对现有实现的描述。

## 已确认：进行中的 Segment 持续呈现

活动结束前即可交付已观测区间；同一 Segment 的后续快照沿用事实身份，更新其时间或内容。上传周期不作为活动切分依据。

用户倾向以 SDK 创建的 Segment 对象表达对同一事实的后续操作，具体接口继续讨论。
该对象对应一条 Segment Fact，不是 Fact Stream：Stream 容纳同一来源、同一 Subject 的同一家族事实，可以包含多条 Segment。

用户明确允许重新设计 SDK 与协议结构，不必沿用当前架构；已确定的 Facts 模型保持不变。

## 已确认：观测时间与发送时间分开

Collector 提供已观测到的结束时间，SDK 管理发送频率；发送时刻不自动成为事实的 EndTime。
用户接受崩溃后不推测、补齐进行中活动的真实结束时刻。已保存的区间仍然有效，未保存的尾部不因此被补造。

## 已确认：并行 Segment 与活动切换

SDK 允许 Collector 同时持有多个 Segment 对象，各自独立更新。某个具体观测范围是否互斥由 Collector 的业务决定，不作为整个 Collector 或 Stream 的通用限制。

Collector 根据实际观测决定活动结束或开始另一条 Segment；周期性交付只发送已有事实的最新快照，不因上传间隔创建新的事实身份。

## 尚待讨论

- Segment 对象的具体操作。
- Subject 的确切含义：设备、应用、账号及采集宿主之间的关系如何表达；先澄清此项，再讨论一个 Collector 实例对应多少 Subject。
- SDK 的配置、Subject/Stream 绑定和生命周期接口。
- 协议版本、能力协商、Package/Artifact 哈希与消息重放机制的取舍。

不把尚待讨论项自动视为保留或删除决定；不改变已确定的 Facts 存储模型。具体接口及协议改动在讨论收敛后实施。

## 已确认：SDK 管理 FactStream

FactStream 继续承担 Facts 模型中的归属关系，由 SDK 管理；Collector 作者无需显式打开 Stream、管理 StreamId 或处理重连后的流恢复，日常接口直接操作事实对象。

必要的观测身份如何提供、一个运行实例对应多少 Subject，以及 Stream 的具体划分规则，仍待讨论。

## 当前讨论场景：设备与账号观测

用户提出 Mac 上的 System/Browser、Windows 上的 System/微信，以及 Server Hub 通过 VRChat 账号观测 MetaQuest3 游玩活动等场景，要求核对 Subject 和既有 AppIdentity 能否自然承载这些关系。

用户补充未来可预见的微信步数：数据由移动设备采集，但希望表达本人的步数。需要区分数据获取入口、采集设备与事实描述对象；不能预先认定接口接触的设备或账号就是 Subject。尚未据此确定 Subject 定义或新增 Measurement 实现。

“一实例一 Subject”尚未被本轮确认。现有 ADR-041 规定了这一关系，本轮先重审其语义前提；暂不增加 Subject 嵌套结构或新的应用关联字段。

## 相关文档

- [Feature of Interest 标准研究](feature-of-interest-research.md) — 标准定义、适用边界及本项目场景推演。
- [设备、账号与本人数据的观测模型建议](collector-observation-model-proposal.md) — 观测业务模型已确认，统一对象存储部分保留为已暂停的历史候选。
- [Collection 术语](../../collection/CONTEXT.md)
- [ADR-017：轻薄采集器与本地 Hub 的初始目标](../adr/017-activity-segment-pluggable-collectors.md)
- [ADR-040：现有 Collector Runtime 与协议决定](../adr/040-collector-runtime-and-protocol-foundation.md)
- [ADR-055：已确定的 Facts 存储模型](../adr/055-fact-storage-by-family.md)
