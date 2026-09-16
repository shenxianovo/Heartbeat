# Heartbeat 记录领域

Heartbeat 保存一个人在数字世界中的异构活动痕迹，以供重放与分析，但不假设任何一条痕迹都能完整描述人的实际活动。

## 领域语言

**Owner**:
Heartbeat 中一个 Timeline 的数据主体，由 Auth 签发令牌中经验证的 UUID `sub` 唯一标识。
_避免使用_: User、用户名、账号

**Timeline**:
一个 Owner 的完整记录空间，其中的记录可以一起重放。设备、会话、项目或时间范围的变化不会产生新的 Timeline。
_避免使用_: User、人员关系图、会话时间线

**Collector**:
Timeline 中一个 Collector 实现与一个 Target 的稳定绑定。只要 Collector 实现和 Target 不变，进程重启、重新安装或凭据变化都不会产生新的 Collector。
_避免使用_: Recording Source、Observer、设备、安装实例

**Target**:
由 Collector 定义并规范化的、其主要观测对象的稳定身份。Heartbeat 不解释 Target 的内部格式，只按 Collector 提供的规范形式标识 Collector 绑定；Target 不是通用实体，也不是 Collector 安装实例。
_避免使用_: Instance、安装标识、实体

**Track**:
Timeline 上的一条同类数据轨道，汇集一个 Collector 产生、由同一数据协议解释且具有相同时间行为的 Record。它可以包含多个观测对象，不要求同一时刻只有一条 Record。
_避免使用_: Observation Track、消息流、展示分组

**Time Mode**:
Record 占据一个时间点还是一段时间区间。取值为 Point 或 Range。
_避免使用_: Temporal Shape、State、Interval

**End Mode**:
Range 的结束位置如何得到：由本条 Record 明确给出，或由下一条 Record 的开始位置推导。

**Record**:
Track 中具有稳定逻辑身份、符合其数据协议的一份观测结果，可以表达时间点或已确认持续的区间，内容与时间可被后续信息更正而仍是同一份结果。Record 表达 Collector 规范化后的观测，不表达对人的活动解释。
_避免使用_: Observation Record、Fact、Activity、原始传输消息

**Application Identity**:
把不同平台的应用标识解析为同一个应用的跨平台身份。它是可修正的解析结果，不是 Record 保存的平台原生标识。
_避免使用_: 可执行文件名、Bundle ID、Package Name

**Foreground Application Observation**:
某段已确认时间内设备系统报告的前台应用读数。它不表示人的注意力或实际活动。
_避免使用_: 前台活动、用户当前活动

**Foreground Window Observation**:
某段已确认时间内设备系统报告的前台窗口读数，窗口标题是其观测属性。它与前台应用观测分开，不表示人的注意力或实际活动。
_避免使用_: 前台应用字段、用户关注窗口

**站稳（Title Dwell）**:
一个新的窗口标题读数要连续保持一段时长才被承认为观测值；没站稳的读数被前一个区间吸收，不成为 Record。它只推迟写入，承认后的区间仍然从这个标题第一次出现的时刻算起。
_避免使用_: 标题去重、相似度合并、防抖后的新标题

**Application Asset**:
与应用关联、可被更新和复用的非时间性展示资料，例如应用图标。它不是活动观测，也不声称还原某条历史 Record 当时的外观。
_避免使用_: Record、应用活动、历史快照

**Input Event**:
Collector 观测并规范化的单次非文本物理键盘或鼠标事件。它保留单次事件的时间位置，不在采集时预先聚合为活跃度，也不表达输入的字符含义。
_避免使用_: 文本输入、用户活动、预聚合计数

**结果更正（Result Correction）**:
后续信息对同一份观测结果的修正，而非新增一份独立结果；修正可以涉及历史结果的内容或时间范围，包括数值变小、区间缩短或时间位置改变。
_避免使用_: 新增独立观测、数值累加

**离开信号（Away Signal）**:
锁屏、会话失活或休眠等明确系统状态的观测，用于活动回放中的离开标记；它不证明人实际离开设备，长时间没有输入也不构成离开信号。
_避免使用_: 无输入、空闲、用户不在场

**观测空白（Observation Gap）**:
缺少可靠观测、无法确认相应活动状态的时间段。空白表达未知，不等于没有活动，也不因前后应用相同而成为已确认持续的活动。
_避免使用_: 离开、零活动、连续活动

**Observation Status**:
某项 Collector 能力在历史区间内的已知可用状态，用于解释可能的 Observation Gap；Available 只表示能力可工作，不承诺数据完整。
_避免使用_: 完整性证明、健康检查当前值

**Device Identity**:
Heartbeat 中用于跨 Collector 关联同一台设备的稳定身份，不随系统重装改变。
_避免使用_: 系统安装标识、Collector 身份、机器名称
