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
Track 中符合其数据协议的一份观测记录，可以表达一个时间点的观测或一段已确认持续的观测。Record 保存 Collector 规范化后的观测值，不保存对人的活动解释。
_避免使用_: Observation Record、Fact、Activity、原始传输消息

**Application Identity**:
把不同平台的应用标识解析为同一个应用的跨平台身份。它是可修正的解析结果，不是 Record 保存的平台原生标识。
_避免使用_: 可执行文件名、Bundle ID、Package Name

**Device Identity**:
Heartbeat 中用于跨 Collector 关联同一台设备的稳定身份，不随系统重装改变。
_避免使用_: 系统安装标识、Collector 身份、机器名称
