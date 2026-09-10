# Segment SDK

Browser 是首个使用者。此模块管理 Segment 身份、起点、快照和现有轮转策略；
观测规则留在 window-activity，原有 delivery/protocol 继续负责修订编码、Stream、ACK 与重试。
没有新增协议、持久实体、通用观测对象登记或依赖包。

```ts
const sdk = createSegmentSdk({ source: 'browser', payloadOf: browserPayloadOf })
const segment = sdk.startSegment({ start: observedAt, payload: activity })
segment.update(nextActivity) // 保持身份和起点，替换读数。
const snapshots = segment.observe(observedUntil)
const finalSnapshot = segment.end(endedAt)
```

调用方把快照交给现有 delivery。observe/end 的时间由 Collector 提供，SDK 没有定时器，
不会用发送时刻扩展事实。update 只改变读数，下一次 observe/end 产生包含最新读数的快照。
Browser 以持续的 Chrome 事件订阅为依据，在周期回调中报告仍在观察的各窗口。

Segment 对象内部替换检查点，不修改此前保存的状态。调用方保存 `segment.state`，恢复时交给
`sdk.restore(savedState)`；平铺字段沿用 Browser 现有会话布局，不需要迁移。调用方可读其中的
活动读数，但不操作 id/startTime。关闭窗口时保存终态快照并移除该窗口的检查点。
完整开发扩展 Reload 的本机实测重新建立活动；既有 checkpoint 保留之前已观测区间。

这一步没有形成完整、独立发布的 Collector SDK。浏览器的回调接线、持久化与交付组合仍在当前包内；
跨 Collector/语言公共入口、Event 和 Measurement 待真实使用者需要时提炼。
