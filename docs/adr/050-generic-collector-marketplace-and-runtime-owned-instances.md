# ADR-050: 通用 Collector Marketplace 与 Runtime-owned Instance

## Status: Accepted

## Date: 2026-09-03

## Context

ADR-048/049 已把可选 Collector 从 Host 构建与组合中移除，并完成 VRChat 精确 Web Release；但用户仍需理解
Release URL、手写 Headless Instance JSON、SubjectId 和 Package source。仅增加一个 URL downloader 会把
Registry/Runtime 内部概念泄漏给用户，也无法形成插件市场式安装体验。另一方面，重新在 Hub 或 Frontend 写死
VRChat 会破坏刚建立的宿主解耦。

## Decision

Official Collector Package Registry 增加通用 Catalog，只列出全部托管 Collector 当前最新版的精确 Release。
Catalog Latest 只属于 Web 发现，不表示任何 Host 的 Desired State、Installation 或 Runtime State。每个
Collector 的独立 release workflow 只更新自己的 Catalog entry；Registry 可以认识 Collector，Host 不可以。

Collector Package 增加 Presentation 与 Default Instance Blueprint。Blueprint 以通用字段声明自动安装后要创建
的 SubjectKind、configVersion 和默认 config；Collector 特有默认值留在 Package 内。`Heartbeat.Collection.Hub`
中的共享 Marketplace module 以小 interface 隐藏 Catalog HTTP、Release 下载、校验、解包与 Installation。
Headless 是首个调用者，Desktop 未来复用同一个 module。

用户点击安装时，Host 通过通用路径安装 Catalog Latest、创建一个默认 Collector Instance 并激活；登录界面
完全由 Collector Protocol Authorization Challenge 驱动。`CollectorRuntime` 是动态 Instance 的唯一持久权威，
Hub 重启只从精确 Installation 恢复，不访问 Web。手写 `instances`、`packageDirectory` 和 Headless mapping
账本直接退役，不做兼容迁移。

完整卸载停止并 drain Instance，再删除 Runtime state、Secret、per-Instance data 和 Installation。第一版不做
版本更新、多个 Instance、实例编辑、签名或自动轮询。

## Consequences

- ✅ Hub、Desktop 与 Frontend 不含 VRChat/Browser 分支；增加 Collector 只改变其 Package、Release 与 Registry
  数据。
- ✅ 用户只看到 Catalog 条目和“安装”，不接触 URL、GUID 或 JSON config。
- ✅ Web Latest、Installation、Instance 与 Activation 各自只有一个权威，Latest 不反写运行事实。
- ✅ 同一共享 Marketplace module 可由 Headless 与未来 Desktop 调用。
- ⚠️ Registry 获得一个有意为之的 mutable Catalog Latest；它只用于首次发现，本轮不承诺更新已安装 Package。
- ⚠️ 旧 Headless 配置不再启动，owner 需要备份后清理旧 data/config 并通过管理页重新安装、登录。

## References

- [ADR-048](./048-shared-collector-host-runtime-and-independent-release-units.md)
- [ADR-049](./049-named-optional-collectors-outside-host-composition.md)
- [Collector Marketplace issue](../../.scratch/collector-package-registry/issues/01-static-registry-index.md)
