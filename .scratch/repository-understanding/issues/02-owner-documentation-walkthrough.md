# 02 — Owner 逐层走查仓库文档

Status: ready-for-human

## 走查顺序

1. **仓库/文件夹级**：`README.md` → `CONTEXT-MAP.md` → `docs/architecture/system-overview.md` → `docs/development.md` → runbook index，确认当前产品边界、目录责任与日常入口。
2. **项目级**：按 `Heartbeat.slnx` 和两个 `package.json` 逐项核对 executable、library、测试项目、部署产物与 owner；判断哪些项目需要 README，哪些只需由上层地图指向。
3. **模块级**：优先走查 Collector Protocol Client、Collector Runtime、Headless Instance Pipelines、System InProcess adapter、Browser Delivery、Analytics ingest/projection、Dashboard 数据 adapter；为每个深模块确认 interface、内部秘密、失败语义和测试入口。
4. **历史层**：对照 `docs/architecture/compatibility-debt.md`，逐条裁决继续支持、计划移除或转为稳定边界。

## 完成标准

- [ ] 每份现有架构/开发/项目文档都被标记为保留、更新、合并或删除，且理由明确。
- [ ] 历史 ADR 中指向已退役源文件的链接被标记为历史快照或改为当前替代入口，不让 broken link 冒充现行实现。
- [ ] 复核 collector-plugin-runtime issue 10 的对账结果：Collector Protocol / Fact Model `Draft 0.2` 已明确裁决为定稿规范、继续演进的 draft，或拆分后的 implemented profile / future design。
- [ ] 文件夹责任与实际项目清单一致；同一事实只有一个权威来源，其余文档只链接不复制。
- [ ] 每个关键深模块都有可发现的接口说明与最小验证入口，不要求为浅模块补 README。
- [ ] 走查中形成的术语修正立即同步相关 `CONTEXT.md`；满足 ADR 门槛的真实决策才建 ADR。
- [ ] 走查结果回写本 issue，并把后续改动拆成有 owner、验收与优先级的 issue。
