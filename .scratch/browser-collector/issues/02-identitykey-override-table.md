# 02: IdentityKey 规范化覆写表

Status: ready-for-agent

## Parent

[PRD](../PRD.md)

## What to build

给浏览器扩展的 IdentityKey 规范化加 per-domain 覆写表，处理"query 才是身份"的站点。默认规则（origin + pathname，掐 query/fragment）消灭 utm/时间戳/锚点造成的假碎片，但会把 `youtube.com/watch?v=a` 和 `?v=b` 过度合并——覆写表按域名声明"保留哪些 query 参数参与身份"。

- 覆写表数据驱动（域名 → 保留参数列表），起步至少覆盖 `youtube.com/watch` 保留 `v`；表可扩展，不硬编码进折叠逻辑。
- 完整原始 URL 始终在 Attributes 里（判据可有损，原始数据无损，ADR-012 原则）——覆写规则将来变化时历史数据可重算。
- 规范化是纯函数，配单元测试。

## Acceptance criteria

- [ ] `youtube.com/watch?v=a` 与 `?v=b` 是两个不同 IdentityKey（不再过度合并）
- [ ] 同一 URL 带不同 `utm_*` / fragment 仍为同一 IdentityKey
- [ ] 覆写表新增一个域名规则无需改折叠逻辑代码
- [ ] 规范化函数单元测试覆盖：默认掐参、覆写保留、大小写/尾斜杠等边界

## Blocked by

- [01](./01-browser-extension-e2e.md)
