# Segment SDK

Browser 是首个使用者。此模块管理 Segment 身份、起点、快照和现有轮转策略；
观测规则留在 window-activity；SDK 持久保存单调 Revision 与 lastSnapshot，delivery/protocol
负责完整原生观测的交付、旧流兼容、准确 ACK 与重试。

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
`sdk.restore(savedState)`。同一完整快照重传保持版本，内容/结束时间/终态变化才加一版，
不从 EndTime 比较新旧。调用方把 state 与待发快照原子保存，不自行修改 id/startTime/revision。
关闭窗口时保存终态快照并移除该窗口的检查点。旧状态迁移与完整浏览器重启按
[Browser 缓存兼容](../../cache-compatibility.md)处理，不把停机时间补进活动。

这一步没有形成完整、独立发布的 Collector SDK。浏览器的回调接线、持久化与交付组合仍在当前包内；
跨 Collector/语言公共入口、Event 和 Measurement 待真实使用者需要时提炼。
