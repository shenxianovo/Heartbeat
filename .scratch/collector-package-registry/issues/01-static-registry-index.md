# 01 — 定义最小 per-Package Registry index

Status: done

Owner: Collection / Package Delivery

Priority: P1 — 发布工具与 Runtime 必须对候选身份、文件长度和 hash 使用同一 contract。

## What to build

定义版本化的 per-Package `current.json` schema，列出 PackageId、Version、artifact URL、length 与
SHA-256。提供本地
fixture server，让发布工具与 .NET Runtime 消费同一份正常和损坏样本。MVP 不包含签名、channel、撤回或
SemVer solver。

## Acceptance

- [x] `/packages/{packageId}/current.json` 有 schema version，并提供精确 PackageId、Version、artifact URL、
  length 与 SHA-256；artifact 位于 `/packages/{packageId}/versions/{version}/`。
- [x] 缺字段、未知 schema、重复 PackageId、非法 Version/URL/hash/length 返回稳定结构化错误。
- [x] artifact URL 只允许 Registry 同 origin、指定目录内的 HTTPS 路径；redirect 不得绕过该边界。
- [x] fixture 同时由发布工具测试与 .NET reader 测试消费，覆盖错长度、错 hash 与损坏 Package。
- [x] 本地静态 server 证明 Runtime 可以读取 index 并下载一个 VRChat artifact。
- [x] glossary/ADR/schema docs 与 `git diff --check` 通过，证据追加在 Comments。

## Non-goals

不实现 Ed25519、canonical signature bytes、channel、withdrawn、动态 Registry API、第三方上传或 owner approval。
不复制 host version、protocol 或 capability compatibility matrix；这些由 Package loader 与真实握手判定。

## Comments

- 2026-08-31：实现落在 `collection/hub/Heartbeat.Collection.Hub/Collectors/Delivery/`，没有新建 csproj。
  契约文件：`CollectorRegistryIndex`（记录）、`CollectorRegistryIndexReader`（读）、
  `CollectorRegistryIndexWriter`（写）、`CollectorRegistryBoundary`（URL 边界）、
  `StaticCollectorRegistryClient`（取 index + 下载校验）。`current.json` 形状被冻结为：

  ```json
  {
    "schemaVersion": 1,
    "packageId": "heartbeat.collector.vrchat",
    "version": "0.1.0",
    "artifact": {
      "url": "https://<registry-host>/collector-registry/v1/packages/heartbeat.collector.vrchat/versions/0.1.0/vrchat.zip",
      "length": 1352564,
      "sha256": "<64 hex lowercase>"
    }
  }
  ```

  v1 拒绝任何额外字段（`UnknownField`），演进必须靠 schemaVersion；channel / signature / 兼容矩阵 /
  发布时间因此进不来。

- 2026-08-31：**PackageId 用 `heartbeat.collector.vrchat`，不是 `vrchat`。** 路径形状仍是 ADR-047 的
  `/packages/{packageId}/`，但 `{packageId}` 取 Collector Package manifest 里的真实 PackageId。
  `VRChatPackageBuilder` 与 `.local/headless-data/collector-runtime.json` 里的现存 Instance 都用
  `heartbeat.collector.vrchat`；如果 Registry 层用 `vrchat`，就必须在 issue 03/04 维护一张
  `vrchat → heartbeat.collector.vrchat` 的映射表，那正是第二套 Package 身份权威。reader、发布工具与
  fixture 全部对 packageId 泛化，没有硬编码。

- 2026-08-31：错误 reason 是一个封闭枚举 `CollectorRegistryFailureReason`，共 18 个值：
  `InvalidRegistryBaseUri`、`InvalidPackageId`、`RequestFailed`、`MalformedJson`、`DuplicateJsonProperty`、
  `MissingField`、`UnknownField`、`UnsupportedSchemaVersion`、`PackageIdMismatch`、`InvalidVersion`、
  `InvalidArtifactUrl`、`ArtifactUrlOutsideRegistry`、`InvalidArtifactLength`、`InvalidArtifactSha256`、
  `RedirectOutsideRegistry`、`TooManyRedirects`、`ArtifactLengthMismatch`、`ArtifactHashMismatch`。
  返回类型是 `CollectorRegistryResult<T>`，`Detail` 只是诊断文本，不作为匹配面。

- 2026-08-31：URL 边界规则——artifact URL 必须与 Registry base URI **同 scheme、同 host、同 port**，且
  路径严格等于 `<base>/packages/{packageId}/versions/{version}/{单个文件名}`。URL 还必须是规范形式
  （`AbsoluteUri == 原字符串`），所以 `…/versions/0.1.0/../../x/vrchat.zip` 在归一化改变含义之前就被拒。
  下载时自己跟随 redirect 并对每一跳重跑同一条边界校验，同时复查 `response.RequestMessage.RequestUri`，
  于是调用方即使传入 `AllowAutoRedirect = true` 的 handler 也无法绕过边界。base URI 非 HTTPS 时只允许
  loopback，本地 fixture 用 `http://127.0.0.1:port/` 因此自洽，生产仍必须是 HTTPS。

- 2026-08-31：fixture 落在 `collection/hub/Heartbeat.Collection.Hub.Tests/Collectors/Delivery/`：
  `VRChatSamplePackage` 用 VRChat 自己的 `--create-package` 产出**真实** Collector Package（每次测试运行
  只建一次）；`StaticRegistryFixture` 把它打成 zip 并写出真实目录树，是唯一的样本生成器；
  `StaticRegistryFixtureServer` 用 `HttpListener` 托管该目录树，端口由 OS 分配（TcpListener port 0），
  不 sleep、不放大 timeout。损坏样本覆盖：错 length（截断 / 追加字节）、错 sha256（翻转一个字节）、
  损坏 Package（zip 内容不可加载但 index 对它诚实）、缺字段、未知 schemaVersion、重复属性、
  非法 version/URL/hash/length、越界 artifact URL（跨 origin / 跨 port / 跨 packageId / 跨 version /
  子目录）、跨目录 redirect。同一棵 fixture 树同时被 reader 测试、下载测试与 issue 02 的发布工具测试消费；
  `Stage_PublishedIndex_MatchesWhatTheFixtureTreeServes` 断言两者字节一致，杜绝两份内联 JSON 漂移。

- 2026-08-31：本 issue 只证明「读 index + 下载 + 长度/hash 校验」。解压与安装是 issue 03，测试里刻意没有做。

- 2026-08-31 验收证据（本机 macOS，`dotnet 10.0.302`）：
  - `git diff --check` 无输出。
  - `dotnet build Heartbeat.slnx --no-restore --configuration Debug` → Build succeeded，0 Warning、0 Error。
  - `dotnet test … --filter "FullyQualifiedName~Collectors.Delivery"` → 58 passed / 0 failed
    （其中 issue 01 的 reader + 下载定向与故障注入测试 40 个）。
  - `dotnet test collection/hub/Heartbeat.Collection.Hub.Tests/… --no-build` → 313 passed / 0 failed。
  - `dotnet test Heartbeat.slnx --no-build` → 1094 passed / 0 failed（基线 1036 + 新增 58，无回归）。
  - glossary：`collection/CONTEXT.md` 新增 **Collector Registry Index（采集器注册源索引）**；ADR-047 的
    路径形状未变，无需修订。
