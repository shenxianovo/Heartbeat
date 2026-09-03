# 01 — 通用 Collector Catalog 与一键安装管理

Status: ready-for-human

Owner: Collection / Package Delivery

Priority: P1 — 让用户从通用 Hub 管理页安装第一份 Web Collector，同时把 Registry、Installation、Instance
与 Activation 的权威彻底分开。

## What to build

实现一条不含具名 Collector 分支的 Marketplace 纵切：

1. Official Collector Package Registry 提供版本化 `catalog.json`，列出全部托管 Collector，各条目按 Host
   target 只暴露当前最新版的精确 Release；Collector 自己的 tag workflow 负责更新自己的 Catalog entry，
   不让 Linux、macOS、Windows target 互相覆盖。精确 Release 位于
   `versions/<version>/<os>-<arch>/`，同一 Package version 可以并存多个 target。
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
      display name、summary，以及按 target 唯一的 latest version 与精确 `releaseUrl`。
- [ ] VRChat 独立 tag workflow 在精确 Release 公网可读后更新自己的 Catalog entry；普通 main/PR 不发布，
      较旧 tag rerun 不把 latest 回退。
- [x] Package loader 校验 presentation 与 Default Instance Blueprint；Blueprint 的 configVersion 必须被 Package
      接受，subjectKind 必须被至少一个输出声明支持。
- [x] 共享 Marketplace module 的公开 interface 不出现 Headless、Desktop、Browser 或 VRChat 类型/常量；正常、
      坏 Catalog、坏 metadata、错 length/hash、坏 zip、Package 不匹配均通过该 interface 测试。
- [x] Marketplace 只接受 Catalog 返回的条目，不提供任意 URL 安装入口；Release 与 artifact 必须是 Registry
      同源 HTTPS 精确路径。
- [x] 安装一次自动创建并激活一个 Default Instance；重复安装不重复创建 Instance。
- [x] Headless 重启在 Registry 离线时仍从 Runtime state + Installation 恢复并激活已安装 Instance。
- [x] 安装/Activation 失败不拖垮 Hub，管理面只显示真实阶段和失败原因，并可显式重试。
- [x] 完整卸载先停止/drain，再删除 Instance、Secret、per-Instance data 和 Installation；失败不伪造“已卸载”。
- [x] Headless bootstrap JSON 不再接受 `instances`；`headless-instance-map.json` 无生产读写路径。
- [x] `/hub/api/v1` 与 `/settings/hub` 全部使用通用 Catalog/Package/Instance DTO；生产代码大小写不敏感搜索不含
      `VRChat|Browser` 具名逻辑（Collector 自身、Registry 数据和测试 fixture 除外）。
- [x] 前端用户只需点击“安装”；登录字段完全来自通用 Authorization Challenge；卸载需要二次确认。
- [x] Reference Package/fixture 证明同一 Host 路径可安装、激活、恢复和卸载非 VRChat Collector。
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

### 2026-09-03 — Agent closeout：实现完成，等待真实发布与生产 smoke

- Package manifest 新增可选 `presentation` 与 `defaultInstance`，Marketplace 上架时两者必填；VRChat 与
  Reference ManagedProcess builder 已声明通用默认蓝图。
- `CollectorPackageMarketplace` 位于共享 Hub assembly，公开面只有 `BrowseAsync` / `InstallLatestAsync(packageId)`；
  Catalog 按 Host target 选 latest，release metadata、同源精确 URL、length/hash、zip 与 Package
  identity/presentation 均在模块内校验。
- Runtime 新增列举与完整移除 Instance；Headless 删除手写 Fleet/mapping，安装自动创建默认 Instance，重启按
  Runtime 精确引用离线恢复，卸载删除 Runtime/Secret/data/Installation。
- 管理面迁至 `/hub/api/v1/collectors` 与 `/settings/hub`；UI 不接收 URL/JSON/Subject/Instance 配置，授权字段只来自
  Collector challenge，并提供失败重试与二次确认卸载。
- Backend workflow 已去掉 Hub/Collector 测试；新增 `deploy-hub.yml`，compose 的 Frontend/Hub 不再互相作为
  启动依赖。

本地证据：Hub tests 265 passed，Headless tests 5 passed，VRChat tests 21 passed，Frontend 255 passed + build；
Collector contract、两个 compose config、workflow/YAML/shell 语法与本轮 C# whitespace 验证均通过。完整 solution
第二轮 0 failed；首轮唯一失败为既有 Recap 取消竞态测试，同一测试单独重跑与第二轮完整测试均通过，且本轮未改
其生产/测试代码。

收口审查把 Catalog 从“每 Package 只有一个 latest”加深为“每 Package 按 OS/architecture 唯一的一组
latest”；Marketplace 只选择当前 Host target，Collector workflow 合并自身 target 且保留其他 target，避免未来
Desktop 的 macOS/Windows Release 覆盖当前 Headless linux Release；精确 Release 路径也增加 target 目录，
避免同 version 的 metadata/artifact 相撞。卸载路径显式等待启动阶段结束后再停止
常驻 Activation，消除了“取消 startup token 后直接等待常驻 Completion”的竞态。
旧 Dashboard 的 subject 状态轮询没有改名保留，而是完整删除；Collector 生命周期与授权只存在于显式进入的
`/settings/hub`，该页本轮也不做后台轮询，只保留操作后的单次刷新和用户手动刷新。

Human gate：必须发布一个新的 VRChat tag（旧 0.2.0 不可变且不含新 manifest 元数据），确认公网
`catalog.json` 更新，然后更新服务器的 infrastructure-only Headless 配置，部署 Hub/Frontend，完成
安装 → 授权 → 重启离线恢复 → 卸载 smoke。完成前不标 `done`。
