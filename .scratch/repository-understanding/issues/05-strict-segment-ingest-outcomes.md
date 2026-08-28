# 05 — 让 Segment strict ingest 返回可判定结果

Status: ready-for-agent

Owner: Analytics / Ingest

Priority: P1 — 当前 `skip + 200` 会让 UploadStream ACK 并永久删除未摄入证据。

## What to build

让 Segment 摄入对非法时间、缺失核心身份与既有 Segment Id identity 冲突返回可判定的整批
`400/422`，而不是逐项静默过滤后返回 `200`。UploadStream 已能二分永久拒绝的 batch，并把
poison fact 写入 durable dead-letter；服务端必须给它真实结果。

## Acceptance

- [ ] `SegmentValidationPolicy.Filter` 不再作为服务端静默丢弃门；非法时间、空 Source、空
  IdentityKey 或无效 Id 使整批零 ActivitySegment/AppIdentity 副作用地返回 `422`。
- [ ] 既有 Segment Id 的 Device、Source 或 IdentityKey 冲突使整批返回 `422`，同批合法项不被
  部分提交，也不创建 provisional App。
- [ ] 合法乱序快照、重复快照和批内同 Id 的单调扩展继续幂等收敛，不被误判为 identity 冲突。
- [ ] 真实 HTTP + UploadStream 测试证明：混合 batch 经 `400/422` 二分后，合法项成功摄入，单条
  poison fact 进入 dead-letter，缓存/outbox 不静默蒸发。
- [ ] `server/CONTEXT.md` 删除已退役的 `UsageValidationPolicy`，准确区分整批 contract 拒绝、
  幂等 duplicate 与成功 snapshot upsert。
- [ ] 相关 server/Hub tests 与 `git diff --check` 通过，验证证据记录在本 issue。

## Evidence

- `UsageService.SaveSegmentsAsync` 当前先调用 `SegmentValidationPolicy.Filter`，再对 identity guard
  冲突 `continue`，最后整体 `SaveChangesAsync` 并让 controller 返回 `200`。
- `UploadStream.ProcessBatchAsync` 已对 `400/422` 二分，并在单条永久拒绝时写 dead-letter。
