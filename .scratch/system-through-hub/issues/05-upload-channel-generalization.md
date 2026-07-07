# 05: 上传通道泛化 + drain-fail 修复

Status: ready-for-agent

## Parent

[ADR-020](../../../docs/adr/020-system-collector-through-hub.md)

## What to build

issue 03 后 Collection 剩两条出网流(segments + input-events),两个 adapter = 真 seam。把两个手写上传 service 的同构模板提炼为一个泛型**上传通道**(Upload Channel,见 desktop/CONTEXT.md)module,契约:**喂入一批项,送达,或落离线缓存,否则原样退回**——退回项由调用方重注入源 buffer(hub 按 Id 收敛天然幂等;input buffer 重排队)。compact 为按流策略(segments 出网前 KeepLatest,input-events 不压缩)。

一次性修掉 drain-then-fail 丢数据模式(drain 清空 buffer 后缓存写盘失败→已 drain 项蒸发),并在通道契约层测试。顺势搭车:status 上传 service(无缓存是设计,presence 易逝)并入其 worker,不入通道。

## Acceptance criteria

- [ ] segments 与 input-events 经同一通道 module 出网,行为差异只剩注入的 compact 策略
- [ ] 通道契约测试:送达清空、失败入缓存、缓存写失败原样退回且调用方重注入不丢不重
- [ ] 上传 worker 退化为对通道列表的定时调度(cached 先于 fresh 的顺序进通道或有测试钉住)
- [ ] 原两套 HttpMessageHandler 上传测试合并为通道行为测试,无覆盖缺失
- [ ] status 上传逻辑并入 worker,独立 service 删除

## Blocked by

- [03](./03-monitor-segments-through-hub.md)(与 04 并行)
