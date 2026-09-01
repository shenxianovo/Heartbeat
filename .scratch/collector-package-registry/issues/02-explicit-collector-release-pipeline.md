# 02 — 建立 VRChat 显式 tag release pipeline

Status: ready-for-human

Owner: Build / Release

Priority: P1 — 第一条纵切必须证明 VRChat 不再依赖 Headless/Desktop 一起发布。

## What to build

让 VRChat 只在自己的显式 SemVer tag 上构建当前真实运行平台的 artifact，生成 index 所需的 Version、
URL、length 与 SHA-256 并发布到静态 Web 目录。普通 main CI 只做 dry-run，不发布用户可见候选。

## Acceptance

- [x] `collector-vrchat/vX.Y.Z` 是唯一发布 trigger；错误 package/tag、非 SemVer 使 pipeline fail closed。
  （dirty generated contract 仍由既有 `collector-contracts` workflow 把关，见 Comments。）
- [x] artifact 可独立运行并带 Package manifest；pipeline 计算 length/hash 并生成 issue 01 的 index entry。
- [x] artifact 是当前 Headless 实际环境可运行的 framework-dependent VRChat zip；不生成 self-contained 或
  多平台矩阵。
- [ ] artifact 先上传并可读，随后才替换 index；index 不得指向尚不存在的文件。
  （staging 目录内已是「先写 artifact、后写 index」；真实上传顺序是 issue 07 的人工门禁。）
- [x] 已存在的 Version 不得覆盖；同 tag 重跑只有在远端 artifact 与 index 完全一致时才幂等，否则失败。
- [ ] main/PR workflow 以临时目录完成端到端 dry-run，不写部署中的 Registry。
  （本次刻意不建 workflow，dry-run 是可复制的本地命令；CI 接线记为 issue 07 人工门禁。）
- [x] System Collector 明确排除在 Web release matrix 外。
- [x] Browser、签名、key rotation、撤回与多平台矩阵不进入本 issue。

## Dependencies

与 issue 01 并行开发，但合并前必须消费其最终 schema/fixtures。

## Comments

- 2026-08-31：实现载体是一个新的 .NET CLI 项目
  `collection/tools/Heartbeat.Collection.CollectorRelease`（已加入 `Heartbeat.slnx`），不是 `scripts/` 脚本。
  理由：发布必须判定「这是不是一个合法 Collector Package」和「Runtime 能不能读这份 index」，两个权威分别是
  `LocalCollectorPackage.Load` 与 `CollectorRegistryIndexReader`，都在 .NET 里。用 Node 实现就得把 manifest
  校验和 URL 边界再写一遍，等于造第二个契约权威。工具直接引用 `Heartbeat.Collection.Hub`，发布侧与 Runtime
  侧是同一段代码。测试放在 `Heartbeat.Collection.Hub.Tests/Collectors/Delivery/`，与 issue 01 的 fixture
  同处一地，避免为了共享样本再建一个测试项目。

- 2026-08-31：dry-run（不需要真实 tag、不需要网络、不改任何 production state）：

  ```bash
  dotnet run --project collection/tools/Heartbeat.Collection.CollectorRelease -- \
    dry-run --output /tmp/heartbeat-registry-dry-run
  ```

  它 `dotnet publish --self-contained false` VRChat → 跑发布出的 apphost `--create-package`（这一步本身
  就证明了 framework-dependent 产物能独立运行）→ 按 Package manifest 的 version 推出
  `collector-vrchat/v0.1.0` → 落出完整 staging 树 → 自校验。真实 tag 用 `stage --tag …`，版本号从 tag 剥离
  后必须与 manifest `version` 完全一致，否则 `VersionMismatch` 失败。

- 2026-08-31：自校验的内容——按 Runtime 的 reader 重读已落盘的 `current.json`（含 URL 边界与
  base URI 的 HTTPS/loopback 规则）、从磁盘重算 length 与 SHA-256、把 zip 解开重新过一遍
  `LocalCollectorPackage.Load` 并比对 `PackageContentHash`。index 在写盘前先过一次 reader，所以坏参数
  （例如非 loopback 的 http registry base URI）不会留下半棵目录树。

- 2026-08-31：zip 是**确定性**的（条目按序、固定时间戳、固定压缩级别、保留 unix 权限位），因此同一份
  Package 重复发布字节一致、可幂等；内容真的变了则 hash 变，`VersionAlreadyPublished` 直接失败并要求发新
  tag。实测：同一 output 连跑两次 dry-run，sha256 均为
  `64959a7a5410b71e7fe51609dfdac68a65a0dc003d04ee9d31092378d1d72464`；人为在 staged zip 尾部加一个字节后
  第三次运行退出码 1 并报 `VersionAlreadyPublished`。

- 2026-08-31：registry base URI 是参数（`--registry-base-uri`），默认值是 RFC 2606 的
  `https://registry.example/collector-registry/v1/` 占位符；仓库里不硬编码真实生产域名。真实域名、服务器
  目录、反向代理与上传顺序都写进了 issue 07 的人工清单。

- 2026-08-31：`CollectorReleaseTarget` 只有 VRChat 一个条目，System 与 Browser 都没有 release target；
  `Stage_CollectorWithoutAReleaseTarget_Fails` 用 `collector-system/v1.0.0` 钉住这条。tag slug 只用来选构建
  目标，工具会把目标声明的 PackageId 与 Package manifest 实际的 PackageId 对比，不一致就 `PackageIdMismatch`，
  所以 slug 不会变成第二套 Package 身份。

- 2026-08-31：`dirty generated contract` 没有在发布工具里重复实现——`node scripts/collector-contracts.mjs check`
  已经由既有 `collector-contracts` workflow 在 PR 与 main push 上执行。注意它不会在 tag push 上跑，因为本次
  没有建发布 workflow；「发 tag 前先确认 contracts 检查为绿」记在 issue 07。

- 2026-08-31 验收证据（本机 macOS，`dotnet 10.0.302`）：
  - `git diff --check` 无输出。
  - `dotnet build Heartbeat.slnx --no-restore --configuration Debug` → Build succeeded，0 Warning、0 Error。
  - `dotnet test … --filter "FullyQualifiedName~Collectors.Delivery"` → 58 passed / 0 failed
    （其中 `CollectorReleaseStagerTests` 18 个，含 tag/版本/PackageId/损坏 Package/self-contained/
    重复发布/http 越界与「staging 树被 fixture server 托管后由 Runtime reader 消费」的端到端用例）。
  - `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/… --no-build` → 313 passed / 0 failed。
  - `dotnet test Heartbeat.slnx --no-build` → 1094 passed / 0 failed（基线 1036 + 新增 58，无回归）。
  - dry-run 真实产物：`packages/heartbeat.collector.vrchat/current.json` +
    `packages/heartbeat.collector.vrchat/versions/0.1.0/vrchat.zip`，length 1352564，
    sha256 `64959a7a5410b71e7fe51609dfdac68a65a0dc003d04ee9d31092378d1d72464`，
    package content hash `sha256:130392fc3c4b8265dfc721dfbd1c8c657bc9fbefe7c2a99b29bbffcb18955f65`。

- 2026-08-31：本 issue 是 code complete，但真实 tag、真实上传与 CI 接线是人工门禁，因此状态是
  `ready-for-human` 而不是 `done`（见 `docs/agents/issue-tracker.md`）。

### 2026-09-01 — 双轴复审收口

- **P2「仓库里硬编码了真实域名」已清**：`collection/hub/Heartbeat.Collection.Headless/heartbeat-headless.compose.example.json`
  的 `registryBaseUri` 曾是 `https://shenxianovo.com/collector-registry/v1/`（既违反本 issue 的「仓库里不硬编码
  真实生产域名」，也与 PRD 写的 `heartbeat.shenxianovo.com` 不一致）。已改回与发布工具默认值一致的占位符
  `https://registry.example/collector-registry/v1/`；真实 base URI 仍是 issue 07 的人工门禁，由 owner 在部署时
  决定并作为 `--registry-base-uri` 传入。
- `registryBaseUri` 未配置时手动 CheckNow 返回 `RegistryNotConfigured` 是**期望默认**，本轮未改：没有 Registry
  的 Hub 不该假装有一个，已安装候选照样可以批准。
- Status 保持 `ready-for-human`：本 issue 的人工门禁没有变——真实 `collector-vrchat/vX.Y.Z` tag 的推送与首个
  artifact 上传、上传顺序、以及是否给 tag 接 CI，全部记在 issue 07 的清单里。
