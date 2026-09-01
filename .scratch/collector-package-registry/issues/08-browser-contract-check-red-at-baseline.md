# 08 — contracts check 起点即为红：Browser 打包产物与源不同步

Status: ready-for-agent

Owner: Collection / Browser

Priority: P1 — 它是 issue 02 的发布前人工门禁（「发 tag 前先确认 contracts 检查为绿」）的直接阻塞项，
不修好就不能真实发布 `collector-vrchat/vX.Y.Z`；同时它让既有 `collector-contracts` workflow 在本机不可复现为绿，
使这条门禁当前不可判定。

## 现象与真实报错

```bash
node scripts/collector-contracts.mjs check
# exit code 1
# Browser source and packaged extension differ; run npm run build and sync dist into Package/browser-extension
```

`scripts/collector-contracts.mjs` 逐字节快照比较：

- `collection/collectors/Heartbeat.Collector.Browser/dist/`（`npm run build` 输出，**未被 git 跟踪**）
- `collection/collectors/Heartbeat.Collector.Browser/Package/browser-extension/`（已跟踪的打包 payload）

实际差异（`diff -rq`）只在 `background.js`（42459 vs 42898 字节）：本机 `dist/background.js` 时间戳为
2026-08-30 09:38，缺少已进入 `src/` 与打包 payload 的 `gapId` 相关代码（`Gap` 身份改为按 `gapId` 比较、
`attempt.gapId` / `uuidv7()` 赋值），并且 `ROTATE_AFTER_MS` 仍是旧的内联常量而非 `rotationPolicy`。
也就是说 `src/` 与 `Package/browser-extension/` 一致，**红的是过期的本机 `dist/`**；在没有 `dist/` 的干净
worktree 里该检查会更早失败为 `Browser dist is missing; run npm run build before contract check`。

## 在 `e78d0cf` 即已存在（不是本轮改动引入）

- 本轮工作起点是 `e78d0cf`，收口于 `723f9e0` / `4ea65a4` / `aafa903`；
  `git diff --stat e78d0cf..HEAD -- collection/collectors/Heartbeat.Collector.Browser` **无输出**，
  本轮没有触碰 Browser 源、打包 payload 或 contracts 脚本。
- `dist/` 未被跟踪，是 2026-08-30 的本机构建残留，早于起点，也不随本轮任何 commit 变化。
- 结论：这条红是起点就存在的既有债务，本轮只做登记，不在文档 commit 里修产物或跑打包。

## 为什么它挡住 issue 02

issue 02 的 Comments 明确把 dirty generated contract 的把关外包给既有 `collector-contracts` workflow，并把
「发 tag 前先确认 contracts 检查为绿」记为人工门禁（清单在 issue 07）。只要该命令为红：

- 门禁的字面条件不满足，owner 无法合法推送真实 `collector-vrchat/vX.Y.Z` tag；
- 即使 VRChat 纵切与 Browser 无关，门禁是仓库级的，不区分 Package；
- 更糟的是它当前**不可判定**：红既可能来自过期 `dist/`，也可能来自真实的源/产物漂移，人工无法只看退出码区分。

## 验证命令

```bash
# 现状（只读，应为 exit 1）
node scripts/collector-contracts.mjs check

# 定位差异侧（只读）
diff -rq collection/collectors/Heartbeat.Collector.Browser/dist \
         collection/collectors/Heartbeat.Collector.Browser/Package/browser-extension
```

## 修好的判据

- [ ] 在 Browser 项目重新 `npm run build`，并把 `dist/` 与 `Package/browser-extension/` 同步到逐字节一致。
- [ ] `node scripts/collector-contracts.mjs check` exit code 0，无输出报错。
- [ ] 若同步导致已跟踪的 `Package/browser-extension/` 发生变化，单独作为 `build(browser):` commit 提交，
      并说明变化来自哪次 `src/` 变更；若无变化，则记录「红仅源于未跟踪的过期 `dist/`」这一结论。
- [ ] `dotnet build Heartbeat.slnx --no-restore -c Debug` 仍为 0 Warning / 0 Error。
- [ ] 在本 issue 记录证据后，回到 issue 02 / 07 把「发 tag 前 contracts 为绿」标记为已满足。

## Dependencies

不依赖本 feature 的其他 issue；反向阻塞 issue 02 的发布门禁与 issue 07 的真实 tag/上传清单。

## Comments

- 2026-09-01：本 issue 由「开发期最小 VRChat Collector Web Delivery 纵切」双轴复审的收尾发现，此前只存在于
  交接汇报中，未落进仓库，现按 `docs/agents/engineering-friction.md` 的 friction closeout 登记为独立债务。
  Status 取 `ready-for-agent`（见 `docs/agents/triage-labels.md`）：修复路径完全确定——重新构建、同步产物、
  跑同一条命令验证为绿，不需要真实设备、账号或发布权限。
