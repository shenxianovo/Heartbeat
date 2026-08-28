# Collector Fact Contracts

`facts/` 是 Collector Fact Schema 的唯一权威来源。Package 内的 schema 副本和最终 `collector-manifest.json` 都由 `scripts/collector-contracts.mjs stage ...` 在 `obj/` 或发布 staging 目录生成，不提交生成副本。

常用检查：

```bash
npm run build --prefix collection/collectors/Heartbeat.Collector.Browser
node scripts/collector-contracts.mjs check --base-ref origin/main
```

同一 `(schemaId, schemaMajor, schemaRevision)` 不允许改变 hash。兼容变更增加 `schemaRevision`；破坏性变更增加 `schemaMajor`，并同步修改引用它的 manifest template 与 producer/projector 行为测试。
