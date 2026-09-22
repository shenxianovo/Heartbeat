# .NET 桌面打包调研结论

调研日期：2026-09-20。本文只保留当时调研中仍有长期价值的结论。项目随后已从 Avalonia 迁移到 AppKit / WinUI，当前实现以 [ADR-0017](../adr/ADR-0017-native-desktop-interfaces.md)、[ADR-0018](../adr/ADR-0018-developer-cli-packaging.md) 和[本地开发文档](../development.md#本地打包)为准。

## 结论

`.NET SDK` 没有同时完成 macOS 与 Windows 的应用包、签名、公证、安装和自动更新的统一发布命令。发布链应区分：

1. `dotnet publish` 生成指定 RID 的运行产物；
2. 平台工具组装 `.app` 或 Windows 应用目录及安装包；
3. 平台信任链处理签名、公证和发布者身份；
4. 产品确认需要后再单独选择安装与自动更新方案。

Heartbeat 当前只建设本地开发打包：Developer CLI 统一命令和失败处理，macOS 与 Windows 仍在各自宿主系统上使用平台 SDK。macOS 开发包使用稳定的本地签名身份，见 [ADR-0020](../adr/ADR-0020-stable-macos-development-signing.md)；Windows 产出 self-contained 目录。正式安装器、发行签名、公证和自动更新尚未进入范围。

## 当时比较过的方案

| 方案 | 结论 |
| --- | --- |
| `dotnet publish` + 小型平台编排 | 当前采用。依赖最少，但平台应用结构、签名顺序和未来发行步骤需要自行维护。 |
| Avalonia Parcel | 曾适合 Avalonia 技术栈，但 CLI 需要商业许可；项目迁移到原生 UI 后不再适用。 |
| Velopack | 适合已经确认需要跨平台安装和应用内更新的产品；会进入应用启动、退出和更新生命周期，不应只为开发打包引入。 |
| Windows MSIX + App Installer | 适合 Store、企业部署、package identity 和 Windows 原生更新；需要作为 Windows 发行决策单独评估。 |
| .NET MAUI / Uno Platform | 属于 UI 与运行时迁移，不是可直接套用的独立打包器，因此不因打包需求采用。 |
| Native AOT | 是发布模式，不是安装器；兼容性和收益应作为独立性能决策验证。 |

未来开始正式发行时，需要分别确认 macOS 分发容器、Developer ID 与公证，以及 Windows 安装和更新渠道；不要让两个更新系统管理同一安装渠道。

## 关键资料

- [.NET application publishing overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [Publish .NET apps for macOS](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)
- [Apple：Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)
- [Windows packaging overview](https://learn.microsoft.com/en-us/windows/apps/get-started/intro-pack-dep-proc)
- [Velopack packaging overview](https://docs.velopack.io/packaging/overview)
