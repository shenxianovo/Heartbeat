# 05 — 让 Segment strict ingest 返回可判定结果

Status: needs-triage

Owner: Analytics / Ingest

Priority: P1 — 当前 `skip + 200` 会让 UploadStream ACK 并永久删除未摄入证据。

## What to build

让 Segment 摄入对非法时间、缺失核心身份与既有 Segment Id identity 冲突返回可判定的整批
`400/422`，而不是逐项静默过滤后返回 `200`。UploadStream 已能二分永久拒绝的 batch，并把
poison fact 写入 durable dead-letter；服务端必须给它真实结果。

## Acceptance

- [x] `SegmentValidationPolicy.Filter` 不再作为服务端静默丢弃门；非法时间、空 Source、空
  IdentityKey 或无效 Id 使整批零 ActivitySegment/AppIdentity 副作用地返回 `422`。
- [x] 既有 Segment Id 的 Device、Source 或 IdentityKey 冲突使整批返回 `422`，同批合法项不被
  部分提交，也不创建 provisional App。
- [x] 合法乱序快照、重复快照和批内同 Id 的单调扩展继续幂等收敛，不被误判为 identity 冲突。
- [x] 真实 HTTP + UploadStream 测试证明：混合 batch 经 `400/422` 二分后，合法项成功摄入，单条
  poison fact 进入 dead-letter，缓存/outbox 不静默蒸发。
- [x] `server/CONTEXT.md` 删除已退役的 `UsageValidationPolicy`，准确区分整批 contract 拒绝、
  幂等 duplicate 与成功 snapshot upsert。
- [x] 相关 server/Hub tests 与 `git diff --check` 通过，验证证据记录在本 issue。

## Original evidence

- 修复前 `UsageService.SaveSegmentsAsync` 先调用 `SegmentValidationPolicy.Filter`，再对 identity guard
  冲突 `continue`，最后整体 `SaveChangesAsync` 并让 controller 返回 `200`。
- `UploadStream.ProcessBatchAsync` 已对 `400/422` 二分，并在单条永久拒绝时写 dead-letter。

## Comments

- 2026-08-30：Analytics 在 Device/AppIdentity 副作用前执行整批 shape/time validation；读取既有
  Segment Id 后先完成 Device/Source/IdentityKey preflight，再解析 provisional App。冲突和非法项统一
  返回 `422`，HTTP ingest transaction 同时回滚尚未登记的请求 Device，合法 snapshot upsert 语义保持
  不变。
- 真实 TestServer HTTP + `UploadStream` 从当前 schema 的 durable segment cache 重放
  `[valid, poison, valid]`：服务端整批 `422` 后二分，2 条合法 Segment 摄入、poison 进入可重启读取的
  JSON dead-letter，cache 清空仅对应已摄入或已隔离事实。
- 自动验证：`dotnet test shared/Heartbeat.Core.Tests/Heartbeat.Core.Tests.csproj --no-restore`（30/30）；
  `dotnet test server/Heartbeat.Server.Tests/Heartbeat.Server.Tests.csproj --no-restore`（449/449）；
  `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/Heartbeat.Collection.Hub.Tests.csproj --no-restore`
  （205/205）；`dotnet build Heartbeat.slnx --no-restore --configuration Debug`（0 warnings / 0 errors）。

### 2026-08-30 — closeout 复审重开

HTTP 行为验收仍有覆盖，但 `SegmentController` 直接持有 `AppDbContext` 并编排 `DeviceService`、
`UsageService` 与事务，原子 ingest interface 没有收敛到 Analytics application module。该 Feature
Envy 使事务不变量和测试 seam 分散在 Controller；在深 module 接管整批 ingest、相关测试重新通过前，
本 issue 恢复为 `needs-triage`。

## Reopened acceptance

- [ ] 原子 strict ingest 的事务、Device/AppIdentity/ActivitySegment 副作用与 contract rejection
  收敛到一个 application module interface；Controller 只负责 HTTP 映射。
