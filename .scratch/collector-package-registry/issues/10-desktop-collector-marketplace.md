# 10 — Desktop 通用 Collector Marketplace

Status: ready-for-agent

Owner: Collection / Desktop Management

Priority: P1 — 让 Desktop 成为共享 Marketplace 的第二个真实调用者，同时保持宿主 UI 不认识任何具名可选
Collector。

## What to build

在 Windows 与 macOS Desktop 的共享原生管理界面中接入现有 `ICollectorPackageMarketplace` 与
`CollectorRuntime`：

1. 按当前 Desktop OS/architecture 浏览官方 Catalog，显示所有适配 target 的可选 Collector 最新版。
2. 点击安装后，由共享 Marketplace 完成下载与 Installation，再按 Default Instance Blueprint 自动创建一个
   Machine Subject 的默认 Instance；用户不输入 URL、JSON、SubjectId 或 InstanceId。
3. 已安装 ExternalHost Collector 没有 Activation 时显示“等待连接”；有 Ready Activation 时显示
   “运行中 · N 个连接”；稳定错误只用短标题，完整 failure/identity 放进展开诊断。
4. 完整卸载复用 Runtime 的通用停止与删除路径，并要求二次确认。System BuiltIn 不进入 Catalog，也没有
   Web 安装或卸载动作。

## Acceptance

- [ ] Windows/macOS 使用同一个共享 presentation/ViewModel 行为；平台 head 只提供 Marketplace target、
      Machine Subject、数据目录和打开原生界面所需 adapter。
- [ ] Desktop 直接调用与 Headless 相同的 Marketplace interface；不复制 Catalog HTTP、release 下载、hash、
      解包、Installation 或 Default Instance 创建逻辑。
- [ ] UI、主题、Desktop state 与 platform head 的生产代码不含 Browser、VRChat、Chrome、Edge 分支或常量；
      卡片完全来自 Catalog/Runtime 通用 DTO。
- [ ] 一键安装成功后只有一个 Runtime-owned 默认 Instance；重复点击不产生第二个 Instance。
- [ ] ExternalHost 的无连接、N 个连接与身份冲突分别显示简短状态；随机 `externalHostIdentity` 不出现在主
      卡片，只进入诊断详情。
- [ ] 安装/下载/Activation 失败不阻止 Desktop 或 System Collector 启动，也不伪造“运行中”；可以显式重试。
- [ ] 卸载二次确认后停止全部 Activation，并只在 Runtime/Secret/data/Installation 都删除成功后显示未安装。
- [ ] 不增加 `manualSetup`/`manualRemoval`、打开 Package 目录或执行 Package 命令的 UI。
- [ ] System 继续只随 Desktop Release；Desktop publish/package 断言仍只有 System BuiltIn，不把 Web
      Collector 重新打进 Desktop 制品。
- [ ] 用 Reference ManagedProcess 与 Reference ExternalHost Catalog fixture 证明 UI/管理逻辑不依赖具体
      Collector；Windows/macOS composition 与 packaged-host startup smoke 保持通过。

## Non-goals

- 不实现安装后的 Package 更新、版本选择、自动轮询或通知。
- 不提供多个 Instance、Instance 编辑、原始 JSON config 或高级 Subject 管理。
- 不自动操作 Chrome/Edge 扩展管理页，也不提供 Browser 专属文案。

## Dependencies

- [issue 01](./01-static-registry-index.md)：共享 Marketplace module 与通用 Catalog。
- [issue 06](./06-browser-external-host-update.md)：通用 ExternalHost 状态、连接计数与可卸载生命周期。
- [ADR-051](../../../docs/adr/051-generic-external-host-identity-and-browser-delivery.md)：Desktop 与 ExternalHost 的
  产品语义。

## Comments

### 2026-09-04 — Grill closeout

Owner 选择 Desktop 原生界面直接复用共享 Marketplace/Runtime，不把管理动作代理到 Analytics；状态必须简短，
详细诊断可展开。当前是单用户自用，不建设 Package 声明的人工加载说明或打开目录按钮。
