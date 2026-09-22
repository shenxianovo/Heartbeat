# macOS 开发凭据调查结论

调查日期：2026-09-22。开发凭据已经按 [ADR-0022](../adr/ADR-0022-macos-development-credentials.md) 完成决策；本文只保留问题证据和方案取舍，不再承担实施契约。

## 现象与根因

使用固定自签名证书后，Heartbeat Dev 的 designated requirement 保持稳定，但内容变化后的新构建仍会在读取既有 Keychain API key 时请求授权。只读系统日志显示，file-based Keychain 将自签名构建识别为随内容变化的 `cdhash:` partition；用户此前选择“始终允许”，也不能覆盖新的 CDHash。

调查没有读取凭据内容。原始本机证据保存在：

- [证据 manifest](../../.artifacts/research/macos-development-credentials-20260922/manifest.json)
- [签名元数据](../../.artifacts/research/macos-development-credentials-20260922/signature.txt)
- [securityd 日志](../../.artifacts/research/macos-development-credentials-20260922/partition-log.txt)

Apple 区分 file-based keychain 与显式使用 entitlement/access group 的 data protection keychain。公开 Security 源码还显示，file-based keychain 在普通 ACL 之后检查 partition；不满足 Apple 开发或 Developer ID 分类的签名会回退到 `cdhash:`。这些资料解释了本次现象，但不构成对未来 macOS 版本的 API 保证。

- [Apple TN3137：On Mac keychains](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains)
- [Apple Security：ACL partition 检查](https://github.com/apple-oss-distributions/Security/blob/main/securityd/src/acls.cpp#L102-L224)
- [Apple Security：客户端 partition 身份](https://github.com/apple-oss-distributions/Security/blob/main/securityd/src/clientid.cpp#L176-L274)

## 方案取舍

| 方案 | 判断 |
| --- | --- |
| 开发身份使用独立的受限凭据文件 | 已采用。消除开发重建后的 Keychain 交互，但只提供系统用户级文件权限，不等价于应用级凭据隔离。 |
| Apple 签发的开发证书 | 可能获得稳定 Team ID partition；需要账号、profile、打包接入和实机验证，不作为本地开发前置条件。 |
| Data protection keychain | 需要 entitlement、access group、profile 和签名配套，适合未来正式凭据路径评估。 |
| 独立凭据 helper | 会新增安装、IPC、调用方验证和升级责任，本地开发收益不足。 |
| 自动修改 Keychain ACL/partition | 不能为未来变化的 CDHash 建立稳定支持边界，不采用。 |

## 已选边界

macOS 开发 Bundle ID 使用独立 profile 下权限为 `0600` 的 `api-key` 文件；普通 macOS 构建继续使用 Keychain，Windows 继续使用 Credential Manager。开发烟测仍覆盖真实 Auth、采集、交付和 Web 回放，但不证明普通构建的 Keychain 授权、锁定或升级行为。具体选择、权威位置和隔离规则以 ADR-0022 为准。
