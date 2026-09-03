# 01 — 通用 Collector Catalog 与一键安装管理

Status: ready-for-agent

Owner: Collection / Package Delivery

Priority: P1 — 让用户从通用 Hub 管理页安装第一份 Web Collector，同时把 Registry、Installation、Instance
与 Activation 的权威彻底分开。

## What to build

实现一条不含具名 Collector 分支的 Marketplace 纵切：

1. Official Collector Package Registry 提供版本化 `catalog.json`，列出全部托管 Collector，各条目只暴露
   当前最新版的精确 Release；Collector 自己的 tag workflow 负责更新自己的 Catalog entry。
2. Collector Package 声明展示信息和一个可自动创建的 Default Instance Blueprint；Registry/Package 可以认识
   VRChat，Host 与 Frontend 不可以。
3. `Heartbeat.Collection.Hub` 提供共享的 Marketplace module，对调用方只暴露浏览 Catalog、安装最新版和完整
   卸载；内部隐藏 HTTP、Release metadata、下载、长度/hash、解包、Package 校验和 Installation。
4. 点击安装自动完成：下载精确 Release、建立 Installation、按 Blueprint 创建一个 Instance、立即激活。
5. `CollectorRuntime` 是 Instance 的唯一持久权威。Hub 重启后从 Runtime state 打开精确 Installation 并自动
   激活，不访问 Web；不新增 Headless Instance catalog。
6. Headless 对外提供 owner-only 通用管理 HTTP；Frontend 把“登录管理”升级成通用“Hub 管理”，显示可安装
   Collector、已安装/运行事实、重试和 Collector 自己发出的 Authorization Challenge。
7. 完整卸载停止并 drain 该 Package 的默认 Instance，删除其 Runtime state、Secret、per-Instance data 与
   Installation；Catalog entry 保留，可重新安装。
8. 直接退役手写 `instances`、`packageDirectory` 与 `headless-instance-map.json` 路径，不做兼容迁移。

## Domain contract

- **Catalog Latest** 只是 Registry 当前推荐下载的 Release，不表示 Host 已下载、安装或运行它。
- **Installation** 仍以本机文件系统为唯一权威。
- **Collector Instance** 只由 `CollectorRuntime` 持久化；SubjectId 与 InstanceId 均由 Runtime/Hub 生成 UUIDv7。
- **Default Instance Blueprint** 属于 Package，声明通用 `subjectKind`、`configVersion` 与默认 `config`；Host 不按
  PackageId 选择默认值。
- 用户不输入 Release URL、SubjectId、InstanceKey 或 JSON config。

## Acceptance

- [ ] `/collector-registry/v1/catalog.json` 使用 schema v1，按 PackageId 唯一列出托管 Collector，并为每项提供
      display name、summary、latest version、target 与精确 `releaseUrl`。
- [ ] VRChat 独立 tag workflow 在精确 Release 公网可读后更新自己的 Catalog entry；普通 main/PR 不发布，
      较旧 tag rerun 不把 latest 回退。
- [ ] Package loader 校验 presentation 与 Default Instance Blueprint；Blueprint 的 configVersion 必须被 Package
      接受，subjectKind 必须被至少一个输出声明支持。
- [ ] 共享 Marketplace module 的公开 interface 不出现 Headless、Desktop、Browser 或 VRChat 类型/常量；正常、
      坏 Catalog、坏 metadata、错 length/hash、坏 zip、Package 不匹配均通过该 interface 测试。
- [ ] Marketplace 只接受 Catalog 返回的条目，不提供任意 URL 安装入口；Release 与 artifact 必须是 Registry
      同源 HTTPS 精确路径。
- [ ] 安装一次自动创建并激活一个 Default Instance；重复安装不重复创建 Instance。
- [ ] Headless 重启在 Registry 离线时仍从 Runtime state + Installation 恢复并激活已安装 Instance。
- [ ] 安装/Activation 失败不拖垮 Hub，管理面只显示真实阶段和失败原因，并可显式重试。
- [ ] 完整卸载先停止/drain，再删除 Instance、Secret、per-Instance data 和 Installation；失败不伪造“已卸载”。
- [ ] Headless bootstrap JSON 不再接受 `instances`；`headless-instance-map.json` 无生产读写路径。
- [ ] `/hub/api/v1` 与 `/settings/hub` 全部使用通用 Catalog/Package/Instance DTO；生产代码大小写不敏感搜索不含
      `VRChat|Browser` 具名逻辑（Collector 自身、Registry 数据和测试 fixture 除外）。
- [ ] 前端用户只需点击“安装”；登录字段完全来自通用 Authorization Challenge；卸载需要二次确认。
- [ ] Reference Package/fixture 证明同一 Host 路径可安装、激活、恢复和卸载非 VRChat Collector。
- [ ] 本机全量测试、Docker/静态 Registry fixture、真实 VRChat 新版本发布，以及生产安装→登录→重启恢复→
      卸载 smoke 的证据记录完成。

## Non-goals

- 本轮不实现已安装 Collector 的版本更新、回滚、自动轮询、通知、channel、SemVer solver 或 LKG；
- 不支持一个 Package 创建多个 Instance，不提供 Instance 编辑、原始 JSON config 或高级 Subject 管理；
- 不实现签名、第三方市场、动态 Registry 后端、Package 上传或审核；
- 不给 Desktop 增加 UI/调用者，不恢复 Browser 专属 binding；共享 Marketplace module 必须可供未来 Desktop
  直接复用；
- 不兼容或迁移旧 Headless `instances` JSON、mapping 或本地 Package source。

## Dependencies

- [ADR-048](../../../docs/adr/048-shared-collector-host-runtime-and-independent-release-units.md)：共享 Host Runtime 与
  独立发布单元；
- [ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)：宿主不认识具名可选 Collector；
- [issue 02](./02-explicit-collector-release-pipeline.md)：已完成 VRChat 精确 Web Release 纵切。

## Comments

### 2026-09-03 — 旧 current.json reader 规格被 Marketplace 纵切取代

旧 issue 只定义 per-Package `current.json` 和底层 reader，会把 Release URL、SubjectId、Instance 与 JSON config
继续暴露给用户。Owner 确认目标是插件市场体验：Registry 列出全部托管 Collector 与最新版，用户只点安装；
Package 声明默认 Instance，通用 Host 自动完成下载、安装、创建与激活。Catalog Latest 仅是发现事实，不成为
Runtime Desired State。
