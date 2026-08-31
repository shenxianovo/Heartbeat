# 03 — 实现版本目录 Collector Installation

Status: done

Owner: Collection / Package Delivery

Priority: P1 — Runtime 只能运行完整下载并验证过的 Package，不能把半成品叫作 Installation。

## What to build

实现一个窄的 Package installer：读取 issue 01 index、下载精确 artifact、校验 length/SHA-256 与 Package
内部内容，安全解压到独立版本目录，最后写完成标记。模块返回精确 Installation 或结构化失败，不负责
Activation，也不实现全局 solver、journal、离线目录或 cache GC。

## Acceptance

- [x] 精确 PackageId/version/hash 映射到独立目录；目录只有在完成标记存在且内容仍匹配时才是 Installation。
- [x] 下载校验 length/hash；Package loader 校验 manifest/artifacts/schema/declarations；解压拒绝绝对路径、
  `..` 与目标目录外写入。
- [x] 下载、解压或校验失败不触碰当前/LKG；无完成标记的目录可在下次尝试直接清理或覆盖。
- [x] 重复安装同一精确候选幂等；同 Version 异 hash 使用不同目录且不得冒充已有 Installation。
- [x] Registry 不可达、断流、磁盘不足、取消、错 hash 与损坏 Package 返回稳定错误。
  （磁盘不足与其他本地 IO 失败共用 `InstallationStorageFailed`，用解压中途 IO 失败代理，没有真实塞满磁盘，
  见 Comments。）
- [x] 单元、故障注入和进程重启后忽略未完成目录的测试通过。

## Dependencies

依赖 issue 01 的 index contract 与 fixtures。

## Comments

- 2026-08-31：实现全部落在既有 `collection/hub/Heartbeat.Collection.Hub/Collectors/Delivery/`，没有新建
  csproj，也没有接线到 Headless / Desktop（那是 issue 04 / 05）。新文件：
  `CollectorPackageReference`（精确候选：PackageId + Version + artifact SHA-256，唯一形态就是精确形态）、
  `CollectorInstallation`（只能由 store 产出的安装事实）、`CollectorInstallationMarker`（完成标记 codec）、
  `CollectorInstallationStore`（目录布局 + 唯一判定函数 + 发布）、
  `CollectorPackageArchiveExtractor`（安全解压 + 上限）、
  `CollectorPackageInstaller`（下载 → 解压 → loader 复验 → 写标记 → 发布，并记住最后错误）。

- 2026-08-31：**安装目录布局**（根目录权威沿用 Hub 现有 state 目录，即 Headless 的 `dataDirectory`、
  Desktop 的 `DataDirectory`——`collector-runtime.json` 与 `collector-secrets` 所在处，没有新配置源）：

  ```
  <hubStateDirectory>/collector-packages/
    packages/{packageId}/{version}/{artifactSha256}/   ← Collector Installation
      collector-manifest.json …                       ← Package 内容
      collector-installation.json                     ← 完成标记
    pending/{attemptGuid}/
      artifact.zip                                    ← 下载中的字节（不在解压根内）
      content/                                        ← 本次尝试的解压目标
  ```

  同一声明 Version 的不同内容天然落到不同 `{artifactSha256}` 目录，不可能互相冒充。

- 2026-08-31：**完成标记** = 安装目录内的 `collector-installation.json`，内容
  `{ schemaVersion, packageId, version, artifactSha256, packageContentHash }`（严格 JSON：重复属性、未知
  字段、类型不符一律视为不可读）。`packageContentHash` 是 `LocalCollectorPackage` 算出的 manifest 内容
  hash，多存一条使「内容被换掉但标记还在」也能判失败。

- 2026-08-31：**判定 Installation 的唯一函数**是
  `collection/hub/Heartbeat.Collection.Hub/Collectors/Delivery/CollectorInstallationStore.cs:OpenInstallation`。
  它同时要求：候选本身格式合法 → 标记文件存在 → 标记可读且 `Completes(reference)`（schemaVersion +
  packageId + version + artifactSha256 全等）→ `LocalCollectorPackage.Load(directory)` 成功 → manifest 的
  PackageId/Version 与候选一致 → Package content hash 与标记记录一致。任一不满足即不是 Installation，
  `CollectorInstallation` 也没有其他构造路径，所以调用方拿不到半成品。Package 身份仍只有
  `LocalCollectorPackage` 一个权威，这里只做组合。

- 2026-08-31：**发布顺序（对设计约束 2 的一处强化，故记在此）**：标记是「解压 + loader 复验之后写入
  pending content 的最后一个文件」，随后用一次 `Directory.Move` 把整个目录搬到最终位置。这样最终目录一
  出现就是完整的，不存在「目录已就位但标记还没写」的窗口，并发两次同 ref 安装因此不会互相破坏（后到者
  的 move 失败 → 复用先到者的 Installation）。这**不是** journal / fsync / 两阶段提交：没有日志文件、
  没有 fsync 屏障、断电仍可能留下 pending 或半成品目录——而半成品目录按定义不是 Installation，下一次同
  ref 安装会删掉它重建（`Publish` 里「目标已存在且不是 Installation → 删除重试」）。

- 2026-08-31：**安全解压**挡住的具体面（`CollectorPackageArchiveExtractor`，也是仓库里唯一一份解压安全
  实现——`Heartbeat.Collection.CollectorRelease.CollectorPackageArchive.Unpack` 已改为委托它，避免发布侧
  自带一份会漂移的副本）：`..` 与 `nested/../..`、绝对路径 `/x`、盘符 `C:\x` 与冒号（含 ADS 形态）、
  UNC `//host/share/x`、反斜杠分隔符、百分号编码变体（`%2e`/`%2f`/`%5c`，虽然当前没有任何解码器，
  但拒绝它使这条边界不依赖「以后没人加解码器」）、`.`/尾点/首尾空格段、控制字符、符号链接与其他非常规
  entry（unix 高 16 位文件类型 + Windows reparse point 位）、仅大小写不同的重复 entry（否则内容取决于
  文件系统）；每个通过名字规则的 entry 还要用规范化全路径 + 带尾分隔符的 root 前缀（Ordinal，最严方向）
  再比一次。权限位按 0o777 掩码保留（VRChat apphost 的可执行位要留住），**不**保留 setuid/setgid/sticky。
  上限可配（`CollectorPackageArchiveLimits`），默认 **entry 数 4096、解压总字节 256 MiB**；声明总量先快速
  拒绝，实际写入时再按同一上限截断。解压失败或被拒时只可能留下没有标记的目录 → 不是 Installation。

- 2026-08-31：错误 reason 仍是**同一个封闭枚举** `CollectorRegistryFailureReason`（18 → 27），新增 9 个：
  `Cancelled`、`MalformedArchive`、`UnsafeArchiveEntry`、`ArchiveLimitExceeded`、`PackageValidationFailed`、
  `PackageManifestMismatch`、`InstallationMarkerMissing`、`InstallationMarkerMismatch`、
  `InstallationStorageFailed`。没有第二套错误权威；候选格式非法复用 issue 01 的
  `InvalidPackageId`/`InvalidVersion`/`InvalidArtifactSha256`。磁盘不足、权限不足与其他本地 IO 失败都是
  `InstallationStorageFailed`（真实塞满磁盘没有做，测试用「entry 名与已写文件冲突导致解压中途 IO 失败」
  代理）。

- 2026-08-31：**结构化最后错误**是 `CollectorPackageInstaller.LastFailure(packageId)`
  → `CollectorInstallationFailure(PackageId, Reference?, Reason, Detail)`，失败写入、成功清除，一次调用只
  尝试一次（测试用 fixture server 的请求计数钉住「不自动重试、已安装不再下载」）。它目前只在内存里：
  持久化与对外展示属于 issue 04 的 management surface，本 issue 刻意不新开状态文件，以免出现第二个
  lifecycle / 兼容权威。本模块不读写 Desired State、Runtime State 或 LKG，失败也不会删除真实候选。

- 2026-08-31：发现并修掉一处 issue 01 的漂移（`engineering-friction.md` 第 1 类）：被中途掐断的 artifact
  下载抛的是 `HttpIOException`（继承 `IOException`），原来的 `catch (HttpRequestException)` 漏掉它，安装
  路径会把「网络断流」误报成 `InstallationStorageFailed`。现在 `StaticCollectorRegistryClient` 显式把
  `HttpRequestException`/`HttpIOException` 归为 `RequestFailed`，而写目标文件抛出的普通 `IOException`
  仍然是本地存储失败。测试：`Install_DownloadTornMidStream_FailsWithRequestFailed`（先红后绿，见提交
  `7e88bd4` → `3916e29`）。

- 2026-08-31：故障注入与不变量测试（66 个新用例，全部在
  `collection/hub/Heartbeat.Collection.Hub.Tests/Collectors/Delivery/`）：
  - `CollectorPackageInstallerTests`（20，真实 loopback Registry + 真实 VRChat Package）：
    `Install_ExactCandidate_PublishesAVersionDirectoryWithACompletionMarker`、
    `Install_SameExactCandidateTwice_IsIdempotentAndDoesNotDownloadAgain`、
    `Install_SameVersionDifferentContent_UsesADifferentDirectory`、
    `Install_DeclaredLengthDoesNotMatch_InstallsNothing`、`Install_TruncatedDownload_InstallsNothing`、
    `Install_DeclaredHashDoesNotMatch_InstallsNothing`、`Install_DownloadTornMidStream_FailsWithRequestFailed`、
    `Install_CorruptArchive_FailsWithMalformedArchive`、
    `Install_ZipSlipArchive_FailsWithUnsafeArchiveEntry`、
    `Install_ArchiveOverTheEntryLimit_FailsWithArchiveLimitExceeded`、
    `Install_ArchiveOverTheSizeLimit_FailsWithArchiveLimitExceeded`、
    `Install_ArchiveWithoutALoadablePackage_FailsPackageValidation`、
    `Install_PackageDeclaringAnotherVersion_FailsWithManifestMismatch`、
    `Install_RegistryUnreachable_FailsWithRequestFailed`、
    `Install_Cancelled_FailsWithCancelledAndInstallsNothing`、
    `Install_Failure_RecordsOneStructuredLastErrorAndDoesNotRetry`、
    `Install_FailureAfterASuccess_KeepsTheInstalledCandidateAndItsLastError`、
    `Install_AfterASuccess_ClearsTheLastError`、
    `Install_TargetDirectoryHoldsUnfinishedContent_RebuildsIt`、
    `Install_TargetDirectoryHoldsAMarkerForAnotherCandidate_RebuildsIt`、
    `Install_TwoConcurrentInstallsOfTheSameCandidate_ConvergeOnOneInstallation`。
  - `CollectorInstallationStoreTests`（21，手工摆出崩溃/重启后的磁盘状态）：完成标记缺失、内容无标记、
    半成品目录在新 store 实例（= 新进程视角）下仍不是 Installation、标记指向别的 version / 别的 artifact
    hash / 别的 packageId / 别的 packageContentHash / 未知 schemaVersion、标记不可读、标记带未知字段、
    缺 manifest、artifact 内容被改、目录键与 manifest version 不符（冒充）、同 Version 异 hash 互不可见、
    以及 5 个非法候选（`../evil`、`0.1`、`../0.1.0`、`not-a-hash`、`…/..`）在碰盘之前就被拒。
  - `CollectorPackageArchiveExtractorTests`（24）：真实 VRChat 包可解压并被 loader 加载、可执行位保留、
    setuid 不保留、目录 entry 跳过而不报错、默认上限容得下真实 artifact，以及上面列出的每一条攻击面、
    两条上限、非 zip 字节、截断 zip、解压中途 IO 失败、取消。

- 2026-08-31 验收证据（本机 macOS，`dotnet 10.0.302`）：
  - `git diff --check` 无输出。
  - `dotnet build Heartbeat.slnx --no-restore --configuration Debug` → Build succeeded，0 Warning、0 Error。
  - `dotnet test … --filter "FullyQualifiedName~Collectors.Delivery"` → 124 passed / 0 failed
    （issue 01/02 的 58 + 本 issue 的 66）。
  - `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/… --no-build` → 379 passed / 0 failed
    （基线 313 + 66）。
  - `dotnet test Heartbeat.slnx --no-build` → 1160 passed / 0 failed（基线 1094 + 66，无回归；Browser
    未触及，既有 Browser 测试原样通过）。
  - 提交：`851651e` test → `25d2c6f` feat → `7e88bd4` test → `3916e29` fix → 本 docs 提交。
    `851651e` 是刻意「先红」的行为固定提交（实现尚不存在，不可编译），`25d2c6f` 起全绿。

- 2026-08-31：本 issue 的验收全部由自动化测试覆盖，没有遗留人工门禁，故状态是 `done`。API
  （issue 04）、ManagedProcess 接入（issue 05）与真实服务器 smoke（issue 07）不属于本 issue；issue 07 的
  人工清单里补了一条与本 issue 相关的部署注意事项（版本目录树无 cache GC）。
