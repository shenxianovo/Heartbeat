Status: needs-info

# 单次 Collector 回放场景未等到受控应用区间

2026-09-20，主线纵切真实运行共三轮：首轮通过；收紧数据库时间上界和前端同名区间选择后，第二轮在采集阶段超时；只补充诊断后第三轮通过。用户不确定第二轮是否切走前台。不能据此确认 flaky、认定产品故障或宣称问题已修复。

## 复现入口

旧 `collector-replay` 场景和白板 app 已按用户要求移除。当前对应入口为 `./scripts/heartbeat-dev scenario desktop-replay --keep-environment-on-failure`；这次替换不代表历史超时根因已确认或修复。

保持真实 `Heartbeat Dev` 客户端前台，浏览器出现后完成真实 OIDC 登录。测试要求数据库出现正确 Owner/Target、预期应用身份、采集时间范围内且至少两秒的 Record。

## 已有证据

- 失败：`.artifacts/verification/20260920T022143Z-scenario-collector-replay-b7a78587538b429296c37256f9e7e7e3/manifest.json`；`stage.json` 为 `native-collection`；错误为 `Timed out waiting for the controlled application's confirmed interval in PostgreSQL.`。隔离环境已按默认策略清理，当时未保存前台变化和落库进度。
- 补充诊断后的通过：`.artifacts/verification/20260920T022417Z-scenario-collector-replay-4b64e18d917740d6b21e98962e88aee4/manifest.json`；`foreground.json` 仅一次 `true`，持续确认与页面 Record 验收通过，环境已清理。
- 现有证据受仓库 artifact 保留策略管理，上述是带日期的验收快照，不是长期稳定性统计。

## 下一次发生时

当前场景先查看客户端采集状态、队列和数据库时间窗；历史 `foreground.json`、`collection.json` 只适用于旧场景。三个候选分别是前台条件变化、Hub/Auth 交付阻塞和时间窗不匹配。保留环境后仅查询受控应用的元数据，不导出其他原生载荷或认证信息。

## Comments

- 2026-09-20：用户答复“不确定”是否在约 10:22 切换前台，原因保持未定。第三轮成功不覆盖第二轮失败；没有加自动重试或延长超时来掩盖它。
