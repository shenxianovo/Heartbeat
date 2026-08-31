# Heartbeat.Collection.CollectorRelease

把一个已构建的 Collector Package 变成静态 Registry 目录树，并在本地自校验。第一条纵切只有 VRChat
ManagedProcess（[ADR-047](../../../docs/adr/047-lean-development-collector-web-delivery.md)）。

## 为什么是 .NET 工具而不是 `scripts/` 脚本

发布必须回答两个问题，而两个答案的权威都在 .NET 里：

- 这个 zip 是不是一个合法 Collector Package —— 由 `LocalCollectorPackage.Load` 判定，它是 Package 身份的
  唯一权威；
- Runtime 能不能读这份 `current.json` —— 由 `CollectorRegistryIndexReader` 判定，包括 URL 边界。

用 Node 脚本实现发布，就得把 manifest 校验和 URL 边界规则再写一遍，等于给同一个契约造第二个权威。工具直接
引用 `Heartbeat.Collection.Hub`，发布侧和 Runtime 侧永远是同一段代码。

## Dry run（不需要 tag、不需要网络、不改任何已部署状态）

```bash
dotnet run --project collection/tools/Heartbeat.Collection.CollectorRelease -- \
  dry-run --output /tmp/heartbeat-registry-dry-run
```

它会 `dotnet publish` framework-dependent 的 VRChat，跑 `--create-package`，按 Package manifest 里的
version 推出 tag，落出完整 staging 树并自校验（重算 length/SHA-256、按 Runtime 的 reader 重读 index、把 zip
解开重新过一遍 Package loader）。可选参数：`--registry-base-uri`、`--package-directory`、`--configuration`、
`--repository-root`、`--tag`。

## 真实 tag

```bash
dotnet run --project collection/tools/Heartbeat.Collection.CollectorRelease -- \
  stage --tag collector-vrchat/v0.1.0 \
        --package-directory <built package> \
        --registry-base-uri https://<registry-host>/collector-registry/v1/ \
        --output <staging dir>
```

tag 版本必须与 Package manifest 的 `version` 完全一致，否则失败。输出：

```text
packages/{packageId}/current.json
packages/{packageId}/versions/{version}/vrchat.zip
```

`length` 与 `sha256` 永远由构建实际计算。同一个 version 目录里已有不同内容时发布会失败——已发布版本不可覆盖，
修复必须发新 tag。

## 不在这里的东西

- **真实域名、服务器目录、反向代理、rsync**：`--registry-base-uri` 是参数，仓库里不硬编码生产域名；上传与
  路由是 [issue 07](../../../.scratch/collector-package-registry/issues/07-deploy-and-vrchat-smoke.md) 的人工门禁。
- **发布用 GitHub Actions workflow**：本次没有建，tag → build → stage → upload 目前是人工流程，同样记在
  issue 07。
- **安装**：下载后的版本目录、安全解压与完成标记属于 Runtime 侧的 Collector Installation（issue 03）。
  这里的解压只用于工具自校验。
- **签名、channel、撤回、多平台矩阵**：ADR-047 明确排除。
- **System Collector**：走 BuiltIn Delivery 随 Desktop 发布，没有 release target。
