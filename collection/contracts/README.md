# Collector Fact Contracts

`facts/` 是 Collector Fact Schema 的唯一权威来源。Package 内的 schema 副本和最终 `collector-manifest.json` 都由 `scripts/collector-contracts.mjs stage ...` 在 `obj/` 或发布 staging 目录生成，不提交生成副本。

常用检查：

```bash
npm run build --prefix collection/collectors/Heartbeat.Collector.Browser
node scripts/collector-contracts.mjs check --base-ref origin/main
```

Package manifest 使用 schema 文件的原始字节 hash 校验 staging 完整性；演进 baseline 使用规范化 JSON hash，因此缩进、空白和对象字段顺序不会伪装成契约变化。同一 `(schemaId, schemaMajor, schemaRevision)` 不允许改变 JSON 含义。兼容变更增加 `schemaRevision`；破坏性变更增加 `schemaMajor`，并同步修改引用它的 manifest template 与 producer/projector 行为测试。
