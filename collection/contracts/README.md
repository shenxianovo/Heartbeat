# Collection 共享约定

Fact Payload 是 Collector 产生的可扩展 JSON。无需 Schema 文档、注册、版本号、可变路径或演进基线。
原生 Fact 显式携带 Kind、稳定 FactId、具体 Observer 的稳定 CollectorId（不必等于 Runtime InstanceId）、Foi、Aspect、Revision 与家族时间；
Payload 完整承载 Result，Source 可空，Relations 默认空列表。Fact 不依赖旧 Subject/Stream 建立身份。
原生 SDK/协议选择 `facts.observation: 2`，不需要交付分组时允许无 Subject 初始化及空 Outputs。
协议继续校验原生完整性、正数 Revision、合法家族、基本 JSON 与时间、尺寸上限。
同版本重试直接比较已保存内容，低 Revision 不覆盖高 Revision；新增字段可随 Collector 正常发布。

Segment 起点、Event 发生时间保持稳定，final Segment 不能重开。Payload 正常修订不限定路径。
实际活动/输入消费者只读取所需字段，缺失或不适用内容仍作为完整 Fact 保管。

`segment-rotation-policy.json` 仍是跨 Collector 的活动轮转约定。
Package manifest 模板由 staging 补齐 Artifact 大小与文件完整性哈希；这些检查与 Fact 内容无关。

```bash
npm run build --prefix collection/collectors/Heartbeat.Collector.Browser
node scripts/collector-contracts.mjs check
```

参见 [ADR-041](../../docs/adr/041-unified-observation-fact-model.md) 与
[ADR-054](../../docs/adr/054-native-analytics-fact-ingest.md)。

`Kind: null` 明确表示旧输入适配，保留旧 Subject/Stream 入口及旧缓存中的 ObserverId/Target 边界转换；
显式 Kind 的原生输入不能用旧字段补足必填信息。SDK/outbox schema 4 与 Runtime schema 9 保管原生快照，
旧包不能处理新缓存。完整旧数据及缓存退出矩阵由 [Ticket 03](../../.scratch/observation-convergence/issues/03-legacy-fact-and-cache-takeover.md) 承接；
System、Browser、VRChat 实际生产者分别由本轮 04–06 切换。
旧 System Observer/Target 引用编码的历史说明见 [实施记录](../../docs/architecture/system-observation-targets.md)，
当前公共发布接口见 [Collector SDK](../protocol/Heartbeat.Collection.CollectorProtocol/README.md) 与
[Collection Hub](../hub/Heartbeat.Collection.Hub/README.md)。
