# Collector Fact Contracts

`facts/` 是 Collector Fact Schema 的唯一权威来源。Package 内的 schema 副本和最终 `collector-manifest.json` 都由 `scripts/collector-contracts.mjs stage ...` 在 `obj/` 或发布 staging 目录生成，不提交生成副本。

## 文件职责

| 文件 | 约束的事实 |
| --- | --- |
| `browser-active-tab-segment.schema.json` | Browser 当前标签页区间的 `identityKey`、`title` 与站点/URL attributes |
| `system-foreground-segment.schema.json` | System 前台应用区间的应用身份、显示名与窗口标题 |
| `system-input-event.schema.json` | System 不可变输入事件的类型、code set 与 code |
| `vrchat-presence-segment.schema.json` | VRChat 在线区间的 world / instance 与展示字段 |
| `reference-segment.schema.json` | 跨进程 reference Collector 使用的最小测试事实 |
| `fact-schema-evolution-baseline.json` | 锁定上述 schema 的 identity、revision 与规范化语义 hash，防止同版本静默改义 |

`.schema.json` 只约束 Fact payload 与该事实族的演进规则，不约束 Collector Protocol 的消息信封。Package staging 必须保留权威 schema 的完整 basename，例如 `schemas/system-input-event.schema.json`；这样 manifest 引用可以直接追溯到唯一源文件。

常用检查：

```bash
npm run build --prefix collection/collectors/Heartbeat.Collector.Browser
node scripts/collector-contracts.mjs check --base-ref origin/main
```

Package manifest 使用 schema 文件的原始字节 hash 校验 staging 完整性；演进 baseline 使用规范化 JSON hash，因此缩进、空白和对象字段顺序不会伪装成契约变化。同一 `(schemaId, schemaMajor, schemaRevision)` 不允许改变 JSON 含义。兼容变更增加 `schemaRevision`；破坏性变更增加 `schemaMajor`，并同步修改引用它的 manifest template 与 producer/projector 行为测试。
