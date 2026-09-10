# Collection 共享约定

Fact Payload 是 Collector 产生的可扩展 JSON。无需 Schema 文档、注册、版本号、可变路径或演进基线。
新 System Fact 直接携带稳定 ObserverId 和唯一 Target（kind/reference），Stream 保留内部交付与事实身份用途。
改造前第一方输入暂保留 Subject/Stream 兼容。协议继续校验 FactId、正数 Revision、合法家族、基本 JSON 与时间、尺寸上限。
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

新增引用编码、实际承载、缓存升级和任务 05 退出条件见 [System Observer/Target 实施记录](../../docs/architecture/system-observation-targets.md)。
