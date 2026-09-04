# 07 — Browser 独立 Package Release 与 Desktop smoke

Status: ready-for-agent

Owner: Collection / Browser Collector + Release

Priority: P1 — 用 Browser 证明 ExternalHost Collector 可以独立于 Desktop 构建、发布、安装和连接，完成目标
架构的第二条真实纵切。

## What to build

1. Browser Collector 自己识别首版支持的 Chrome/Edge 与 Windows/macOS，并向通用 ExternalHost `hello`
   直接发送稳定 `appIdentityKey` 和 Profile 持久化的 `externalHostIdentity`；不再发送 `appHint`。
2. Browser Package 声明 Marketplace presentation、Machine-scoped Default Instance 与
   `appIdentityKey + externalHostIdentity` identifying dimensions；Artifact 继续完整枚举 sideload payload。
3. 增加只由 `collector-browser/vX.Y.Z` 触发的独立 workflow，构建一份确定性 Package，并把相同字节作为
   Windows x64/arm64、macOS x64/arm64 四个 target 的精确 Release 与 Catalog Latest 发布。
4. 操作者从 Installation 手工 Load unpacked。Package、Host 和 UI 都不定义人工安装/移除说明；Desktop 不
   自动操作浏览器。
5. 完成真实 Desktop + Chrome/Edge smoke，证明安装、等待、单/多连接、重连、事实上传与完整卸载。

## Acceptance

- [ ] Browser 扩展的生产代码使用通用 ExternalHost route/wire shape；不再出现
      `/v1/collector-protocol/browser`、`appHint` 或 Host 专属兼容分支。
- [ ] Chrome/Edge 在 Windows/macOS 上生成对应稳定 `appIdentityKey`；无法可靠识别时 Collector 不开始
      Activation，不让 Hub 猜测产品身份。
- [ ] `externalHostIdentity` 按扩展安装/Profile 本地持久化；Service Worker 重启产生新 ActivationId 但保留
      identity，正常重连复用原 Stream。
- [ ] Package manifest 的唯一默认 Instance 是 Machine-scoped；Chrome、Edge 和不同 Profile 不创建额外
      Instance。
- [ ] Package outputs 只声明 `appIdentityKey` 与 `externalHostIdentity` identifying dimensions；Fact payload
      不复制这些身份字段，页面 `identityKey` 仍表示规范化 URL。
- [ ] workflow 只监听严格的 `collector-browser/vX.Y.Z` tag；普通 main、PR、Desktop tag 与其他 Collector tag
      不发布 Browser，也不构建/发布 Desktop。
- [ ] 一次构建产生一份确定性 zip；四个 target 的 `release.json`/Catalog entry 指向相同 artifact bytes 与
      SHA-256，旧 tag rerun 不覆盖不同字节，较旧版本不回退 Catalog Latest。
- [ ] Browser Release 不要求修改或部署 Desktop、Headless、Frontend、Analytics；Desktop Release 仍不包含
      Browser Package。
- [ ] 自动 fixture 覆盖 Chrome/Edge identity、未知/冲突品牌、Profile identity 持久化、generic handshake、
      两 Profile 并行、同 Profile 重连、ACK/Gap/drain 与 package identity mismatch。
- [ ] 真实 macOS/Windows Desktop 安装后状态先为“等待连接”；Chrome 与 Edge Load unpacked 后分别连接，
      同时运行显示正确连接数，Backend 收到正确 Source/AppIdentityKey/URL identity。
- [ ] 完整卸载后 Runtime/Secret/data/Installation 消失；仍加载的扩展重连得到 `package_not_installed`，
      Desktop 与 System Collector 保持运行。

## Non-goals

- 不实现 Chrome Web Store、Edge Add-ons 或企业策略分发。
- 不实现 Browser Package 更新、热切换、LKG 或扩展自动 reload；新版本需要先卸载再安装。
- 不支持或承诺 Brave、Opera、Vivaldi、Firefox；它们可以在后续真实验证后由 Browser Collector 自己扩展。
- 不增加 Host 侧 Browser/AppHint 映射、具名状态类型、UI 卡片或 setup launcher。

## Dependencies

- [issue 06](./06-browser-external-host-update.md)：通用 ExternalHost route、identity ownership 与卸载语义。
- [issue 10](./10-desktop-collector-marketplace.md)：Desktop 的通用 Catalog/安装/状态界面。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：Browser 独立交付与身份决策。

## Human gate

代码与自动验证完成后保持 `ready-for-human`，直到：

- 推送一个真实 `collector-browser/vX.Y.Z` tag，四个公网 target 的 metadata 与 artifact 可回读；
- 在真实 Windows/macOS Desktop 上至少各完成 Chrome 或 Edge 的 Load unpacked 与连接；
- 至少一台机器完成 Chrome + Edge 或两个 Profile 并行、重连、事实到达 Backend 与卸载后拒绝重连 smoke。

## Comments

### 2026-09-04 — 重写旧 VRChat approval smoke

VRChat 0.2.1 的 Catalog 发布和生产安装/授权/恢复/卸载 smoke 已完成，旧 issue 07 的 approval/LKG 内容不再
成立。Owner 确认下一条目标是完全通用的 ExternalHost + Browser 独立 Package；Host 不能通过任何具名代码
感知 Browser。本 issue 因此只承接 Collector 自身、独立 Release 与真实设备验收。
