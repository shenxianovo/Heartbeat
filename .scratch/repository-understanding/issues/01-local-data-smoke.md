# 01 — 建立恢复数据与新客户端数据 smoke

Status: done

- [x] 检查只输出聚合计数和时间水位，不泄露标题、URL、输入内容或账号内容。
- [x] 历史数据检查覆盖时间范围、必填身份、外键完整性和未来时间。
- [x] system 重叠、语义重复与 App 双写不一致作为可比较质量信号，允许看见存量问题但禁止新客户端让它们恶化。
- [x] 客户端运行前可保存基线，运行后要求 Segment 或 InputEvent 水位推进。
- [x] 持续 Segment 使用相同 FactId 更新时仍可被水位检查识别，不只依赖行数增长。
- [x] 开发指南与 runbook 给出可判定的完成标准。

## Comments

- 2026-08-28：新增 `scripts/smoke-local-data.mjs` 与 `docs/runbooks/local-data-smoke.md`；当前恢复数据 check 通过，完整 baseline→verify 在本轮回归中验证。
