# VRChat Account Collector

实验性的 `vrchat.account` ManagedProcess Collector。它对明确 VRChat 账号产生独立的
`account-location` Segment；账号管理分组保留，不作为新 Fact 的身份。它不是 VRChat 官方集成。

## 目录

- `Program.cs`：stdio 协议入口；`--create-package <dir>` 生成 Collector Package。
- `VRChatManagedCollector.cs`：Activation、授权、轮询与 drain。
- `VRChatApi.cs`：真实 API 与离线 mock adapter。
- `PresenceStateMachine.cs`：presence Segment 的 FactId/Revision 状态机。
- `VRChatPresenceCheckpoint.cs`：跨重启恢复与 Gap。
- `VRChatPackageBuilder.cs`：manifest 与 artifact staging。
- `Dockerfile`：把 Package 构建到宿主目录的入口（构建上下文是仓库根）。

## 本地导出 Collector Package（按需）

日常启动本地栈不需要先执行这些脚本；Headless Hub 会从 Registry 安装已发布 Package。只有修改
VRChat Collector 的打包逻辑、希望在打 tag 前检查 Linux Package 内容时，才需要本地导出：

```bash
./scripts/build-vrchat-package.sh              # 默认输出 .local/collector-packages/vrchat
./scripts/build-vrchat-package.sh --output /srv/heartbeat/collector-packages/vrchat
```

```powershell
./scripts/build-vrchat-package.ps1
```

两个脚本都会先在输出目录的临时 sibling 中用 BuildKit publish 并跑 `--create-package`，验证成功后才替换
带工具 ownership marker 的旧输出；已有的非空未托管目录会被拒绝。必须走容器：manifest 里的
artifact selector 取的是构建进程的 OS/arch，在 macOS/Windows 上直接构建会得到 Headless 容器
选不中的 artifact。

## 独立发布

VRChat 使用专属的稳定版本 tag：

```text
collector-vrchat/vX.Y.Z
```

[`release-collector-vrchat.yml`](../../../.github/workflows/release-collector-vrchat.yml) 在 `linux/amd64` 容器内
用 tag 版本构建 Package，再生成一个可重复的 zip 和不可变 `release.json`。它们发布到：

```text
https://heartbeat.shenxianovo.com/collector-registry/v1/
  packages/heartbeat.collector.vrchat/versions/X.Y.Z/
```

普通 `main`/PR 不发布；共享 CI 通过 VRChat.Tests 对 release assembler 做 dry-run。服务器上已经存在相同
Version 时，只有字节完全一致才允许把 workflow rerun 当成幂等，否则发布失败。这里没有 current pointer、
自动安装、更新 channel、签名或回滚；它们不属于这条显式发布纵切。

本地已有 Package 时，可只生成待发布文件而不上传：

```bash
./scripts/package-vrchat-release.sh \
  --package .local/collector-packages/vrchat \
  --version 0.1.0 \
  --output /tmp/vrchat-release
```

## 验证与当前交付

```bash
dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests
```

Package 是独立于 Headless 镜像的制品：专属 tag 工作流把它发布到 Registry，Headless 再按 Catalog
下载、校验、安装和运行；换 Package 不需要重建 Hub 镜像。本地构建脚本只用于发版前检查，不参与
日常本地栈启动。运行宿主见
[Headless README](../../hub/Heartbeat.Collection.Headless/README.md)，授权边界见
[ADR-043](../../../docs/adr/043-hub-local-interactive-collector-authorization.md)。

## 观测与专有状态兼容

程序和 Package 协商 `facts.observation:2`。新 Fact 使用生产者 UUID、固定 CollectorId/账号 FOI、
`segment`、`account-location`、`vrchat.account`，没有设备/App 依据时 `Relations=[]`。持续位置
递增 Revision，世界/实例/账号变化另开 Fact；同 End 的终态变化也递增 Revision。CollectorId
来自该账号 Collector Instance 的稳定身份，并保存进 checkpoint；恢复不会用当前 Observer 替换它。

`collector-data/<InstanceId:N>/vrchat-presence.json` 当前写 schema 4，保留进行中状态及尚未交给
SDK 的 Facts/Gaps。历史 v1（Active/IdentityKey）、v2（增加 pending Facts/Gaps）、v3
（ActivityKey/可选 ObservedAccountId）经真实读取入口验证、保留 `.vN.bak` 后原子升级。
旧事实保留旧 FactId、Revision 与 `presence` Binding 对应，继续 `Kind=null`；未知账号保持未知。
恢复先重放 pending，再以原 End、Revision+1 收尾 Active，并记录原 End 到恢复时间的
`process_restart` Gap；停机时间不加入旧 Segment，新观测另开事实。

schema 4 发布前先原子写同目录 `collector-data-requirements.json` 的
`facts.observation:2` 要求。Runtime 启动/更新/LastKnownGood 回退均检查它；SDK outbox 排空也不
删除要求。未知格式或可解析但无效的 checkpoint/要求文件拒绝并保全，只有确实坏掉的 checkpoint
JSON 字节沿隔离流程记录恢复 Gap。失败后恢复兼容 Package 并重试，不能删要求文件或自动用旧备份
覆盖新状态。完整规则见[缓存兼容](../../../docs/architecture/observation-cache-compatibility.md)。

## 自动验收与现场安装步骤

自动入口使用既有 mock 外部 API，不访问真实 VRChat 账号；实际运行 apphost、stdio SDK、Runtime、
HTTP 与隔离 PostgreSQL。两个 mock 账号通过 `HEARTBEAT_VRCHAT_MOCK_ACCOUNT_ID` 区分，该变量仅在
`HEARTBEAT_VRCHAT_MOCK=1` 时使用。

```bash
dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests --no-restore
dotnet test collection/hub/Heartbeat.Collection.Hub.Tests --no-restore --filter FullyQualifiedName~ManagedProcessDataRequirementsTests
dotnet test server/Heartbeat.Server.Tests --no-restore --filter 'FullyQualifiedName~VRChatNativeManagedProcess|FullyQualifiedName~VRChatLegacyManagedCheckpoint'
```

现场验收由 owner / Headless 发布维护者承接，当前未执行，Ticket 06 保持 `ready-for-human`。
执行须纳入原 observation-storage 发布门禁，不能把下列步骤当作当前已部署证据：

1. 在批准的安装窗口记录 Runtime/Package 版本、精确 content hash、平台、每个 InstanceId 及其
   实际账号映射；备份 Runtime、整个 `collector-data`（包括要求、outbox、dead-letter、checkpoint、
   `.vN.bak`）和 Secret 存储。证据不包含 cookie、密码或验证码。记录 LastKnownGood 与各目录 schema、
   pending/active/dead-letter 数量、最老待发时间和 owner 批准的最长离线/回退窗口。
2. 通过已批准的安装渠道装入精确新 Package，保留 Instance 和数据目录，在 Hub 管理页完成必要授权。
   对两个独立账号分别记录世界/实例、观测时间与 Fact Id/Revision。核对 Source、账号 FOI、稳定 Observer，
   没有设备证据时关系为空。不要把 Headless 服务器或当前登录账号补给历史。
3. 在隔离验收环境阻断 Analytics 上行，保持采集，确认 pending 增长；停止并重启自己的测试 Collector。
   检查旧事实用原 Id 收尾、重启 Gap、随后新 Id，无停机区间扩张；账号 A/B 的目录、Observer 和结果不串。
   恢复上行，通过 `/api/v1/users/<username>/facts/segments?foiId=<对象UUID>` 读回，核对精确 Id/Revision、
   原始 Result、时间、空 Relations；重复投递不得多出事实，ACK 后待发排空。
4. 在完整隔离目录副本演练旧 Package 手动启动及失败候选的 LastKnownGood 回退，确认
   `collector_cache_incompatible` 且原文件未改；恢复兼容 Package 后继续交付。不得对真实正在采集的
   目录强行降级，不能通过删除 marker、清空状态或替换新文件绕过门禁。
5. 将平台、安装 hash、时间窗口、读回样本、失败/重试结果和剩余差异附到 Ticket 06；现场通过后由
   协调任务核对生命周期。完整生产副本/业务库迁移与资源演练继续属于原发布任务，仍未在本票执行。
