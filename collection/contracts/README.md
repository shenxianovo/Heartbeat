# Collection 共享约定

Fact Payload 是 Collector 产生的可扩展 JSON。无需 Schema 文档、注册、版本号、可变路径或演进基线。
协议保留 Subject/Stream 归属、FactId、正数 Revision、合法家族、基本 JSON 与时间检查、尺寸上限。
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
