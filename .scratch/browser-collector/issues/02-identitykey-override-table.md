# 02: IdentityKey 规范化覆写表

Status: done

## Parent

[PRD](../PRD.md)

## What to build

给浏览器扩展的 IdentityKey 规范化加 per-domain 覆写表，处理"query 才是身份"的站点。默认规则（origin + pathname，掐 query/fragment）消灭 utm/时间戳/锚点造成的假碎片，但会把 `youtube.com/watch?v=a` 和 `?v=b` 过度合并——覆写表按域名声明"保留哪些 query 参数参与身份"。

- 覆写表数据驱动（域名 → 保留参数列表），起步至少覆盖 `youtube.com/watch` 保留 `v`；表可扩展，不硬编码进折叠逻辑。
- 完整原始 URL 始终在 Attributes 里（判据可有损，原始数据无损，ADR-012 原则）——覆写规则将来变化时历史数据可重算。
- 规范化是纯函数，配单元测试。

## Acceptance criteria

- [x] `youtube.com/watch?v=a` 与 `?v=b` 是两个不同 IdentityKey（不再过度合并）
- [x] 同一 URL 带不同 `utm_*` / fragment 仍为同一 IdentityKey
- [x] 覆写表新增一个域名规则无需改折叠逻辑代码
- [x] 规范化函数单元测试覆盖：默认掐参、覆写保留、大小写/尾斜杠等边界

## Blocked by

(无；基础 Browser Collector 已由现行 Collector Protocol 实现。)

## Comments

- 2026-09-08：`normalize.ts` 增加 hosts/path/params 规则表，YouTube 根域、www、m 的 `/watch`
  保留 `v`；路径与参数名/值大小写保持，host 与尾斜杠沿用原规范化，原始 URL 不变。
  规则只匹配显式域名和路径，不扩大到相似域名；重复参数保留，参数顺序由规则决定。
  原测试把不同视频合并固化为“已知限制”，现改为正确行为断言，并补切段、噪声变化不切段及原始 URL 保留测试。
  `npm test -- tests/normalize.test.ts tests/fold.test.ts` 修复前 8 failed / 30 passed；
  修复后 Browser 目录 `npm test && npm run build` → 96 passed，TypeScript 与 Vite 构建通过。
  `fold.ts` 无需修改，历史已存段不重写；新规则随后续 Browser Package 发布生效。本次未 commit 或发布。
