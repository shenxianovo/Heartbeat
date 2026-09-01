# 07 — 部署静态 Registry 并完成 VRChat smoke

Status: ready-for-human

Owner: Release / Operator

Priority: P1 — 域名路由与真实 VRChat 更新需要 owner 操作和观察。

## What to do

在 01–05 与选定 termination truth gate 通过后，部署开发期静态 Registry，发布首个 VRChat artifact，
走通检查、安装、批准、Ready 与失败保持旧 LKG。Browser、签名和生产迁移不在本 issue。

## Preconditions

- issues 01–05 code complete，dry-run 与失败保持旧 LKG 的测试有证据。
- Activation lifetime issue 09、10 完成。
- 当前真实 VRChat PackageId/version/hash 与 LKG 已以只读方式记录。

## Acceptance

- [ ] 独立 Registry deployment 可经 `/collector-registry/v1/` 读取，缓存与反向代理不改 bytes；frontend
  deploy/rollback 不改变 Registry 内容，Registry deploy 也不触碰 `/u/` 页面。
- [ ] Registry 内容来自服务器独立静态目录，不由 Analytics API 或 frontend image 提供。
- [ ] VRChat artifact 由显式 tag 产生；index 只在 artifact 已上传可读后更新。
- [ ] 真实 ManagedProcess smoke：手动检查 → 下载/验证 → authenticated API 批准 → authenticated API
  切换（`POST /hub/api/v1/collector-instances/{id}/package-update/switch`）→ Ready；失败候选不破坏旧 LKG。
- [ ] PackageId、InstanceId、配置、Secret、Fact Stream 与 LKG 保持；不匹配内容走新版本，不猜继承。
- [ ] 不把旧 bundled Package 登记为 Web Installation；它只作为旧 LKG，第一个 Web release 是普通候选。
- [ ] Registry 离线、坏 index、错 hash 与候选启动失败均显示可区分结果。
- [ ] 完成证据追加到 Comments；所有门禁完成前本 issue 保持 `ready-for-human`，PRD 不标 done。

## Manual gates carried over from issues 01–02

2026-08-31 加入。这些步骤必须由 owner 在真实服务器上执行，agent 不代做：

- [ ] 选定真实 registry base URI 并作为参数传给发布工具（`--registry-base-uri`）；仓库里不硬编码域名，
  当前默认值是占位符 `https://registry.example/collector-registry/v1/`。
- [ ] 反向代理把 `/collector-registry/v1/` 映射到独立静态目录，且路径按真实 PackageId 组织：
  `packages/heartbeat.collector.vrchat/…`（不是 `packages/vrchat/…`，见 issue 01 Comments）。
- [ ] 上传顺序：先 `versions/{version}/vrchat.zip` 并确认可读，再替换 `current.json`。发布工具只保证
  staging 目录内的写入顺序，rsync/拷贝顺序在人这边。
- [ ] 发 tag 前手动确认 `node scripts/collector-contracts.mjs check` 为绿——本次没有建发布 workflow，
  `collector-contracts` workflow 只在 PR / main push 上跑，不会在 tag push 上跑。
- [ ] 决定是否需要 CI 接线：`tag → build → stage → upload` 目前完全是人工流程。若要加 workflow，按冻结
  约束它只能是 `workflow_dispatch` / dry-run 形态。
- [ ] 真实 `collector-vrchat/vX.Y.Z` tag 的推送与首个 artifact 的实际发布。
- [ ] 2026-08-31（issue 03 加入）：确认 Headless `dataDirectory` 的磁盘余量，并在需要时人工删除旧的
  `collector-packages/packages/{packageId}/{version}/{artifactSha256}/` 目录与残留 `collector-packages/pending/`。
  安装按精确 version + artifact hash 建独立目录且**没有** cache GC（ADR-047 明确出范围），所以旧版本会一直
  留着；删除没有完成标记的目录总是安全的。

## Comments

- 2026-08-30：本 issue 预先标为 `ready-for-human` 是因为域名和真实设备 smoke 必须由 owner 执行；
  这不表示其代码依赖已经完成。
- 2026-08-31：ADR-047 将第一条纵切缩减为 unsigned VRChat development delivery；Browser、生产签名与
  完整迁移移出本 issue。
- 2026-08-31：issue 01 已 done、issue 02 code complete（`ready-for-human`）。上面「Manual gates carried
  over」记录了它们留下的、必须由人执行的部署与 CI 门禁；本 issue 在这些门禁完成前保持 `ready-for-human`。
- 2026-08-31：issue 05 已 done。批准与"开始使用"是两次 owner 动作：批准之后必须再调一次
  `POST /hub/api/v1/collector-instances/{id}/package-update/switch`，Ready 才算更新成功；从未 Ready 的
  已批准候选不会靠重启接管，所以 smoke 时不要用"重启 Hub"代替这一步。
- 2026-08-31：issue 03 已 done（版本目录安装 + 完成标记，全部由自动化测试覆盖，无新增人工门禁），只给上面
  清单加了一条部署期磁盘/清理注意事项。真实 smoke 仍需 issue 04、05。

### 2026-09-01 — 双轴复审收口

- 例子配置里的真实域名已换回占位符（见 issue 02 同日记录），所以「选定真实 registry base URI」这条人工门禁
  重新是唯一的域名来源，仓库里没有第二处写着真实生产域名的 Registry base URI。
- smoke 的动作数已在 ADR-047 与 PRD 里写明：管理面共四个 owner 动作（Current、手动 CheckNow、Approve exact
  ref、显式 Switch）。批准不隐含接管，Switch 才启动候选，也不能用「重启 Hub」代替——这条已在上面的
  Acceptance 与 Comments 里，本次只是与 ADR/PRD 对齐。
- PRD 的 MVP exit condition 路径已由 `packages/vrchat/current.json` 改成真实 PackageId
  `packages/heartbeat.collector.vrchat/current.json`（P3）；全库已无 `packages/vrchat/` 的残留写法，只剩上面
  「不是 `packages/vrchat/…`」这一处刻意的反面提醒。
- Status 保持 `ready-for-human`：本 issue 的 Acceptance 与「Manual gates carried over」全部需要 owner 在真实
  服务器上执行，agent 不代做。issue 04 已 `done`，其真实域名端到端 smoke 由本 issue 的
  「真实 ManagedProcess smoke」那一条承接。
