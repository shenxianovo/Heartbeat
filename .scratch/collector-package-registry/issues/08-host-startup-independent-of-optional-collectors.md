# 08 — 宿主启动不依赖可选 Collector

Status: wontfix

> 2026-09-02 更正：本 issue 只做到「Browser 缺席时降级」，不是宿主与 Browser 解耦。那一轮之后宿主仍然
> 认识 Browser——Hub 里有 Browser 专属 `CollectorRuntime` 与 protocol handler，Desktop 组合根有
> `browserPackageSourceDirectory` 与 `AddBrowserExternalHostBinding`，两个平台各有 Chrome/Edge 的 AppHint
> 知识，UI 里有 Browser 卡片，startup smoke 还要解析 Browser 状态。下文按当时实现原样保留为历史记录。
>
> 宿主解耦由 [issue 09](./09-named-optional-collectors-out-of-host-composition.md) 完成（决策见
> [ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)），它 supersede 本
> issue。以下四项验收在当前代码里已不再适用，因为它们描述的 Browser 专属路径整体删除了：Browser package
> source 缺失/损坏只降级、未安装时 `hello` 返回 `package_not_installed`、UI 显示「未安装采集器包」、
> smoke 报告里的 Browser `Degraded` 证据。本 issue 未执行的 Desktop Release 门禁由 issue 09 一并承接，
> 本 issue 不再单独推进。

Owner: Collection / Host Runtime

Priority: P1 — ADR-048 的发布单元边界靠它才成立：Desktop 不该因为 Browser 而构建失败或启动失败。

## What to build

把「可选 Collector 缺席」从异常路径变成正常状态：

1. Desktop 构建与产物不再包含 Browser Package，Browser 只是一个可手工侧载的落点。
2. System 作为 BuiltIn Delivery 必须随 `dotnet publish` 产物走，不靠 release workflow 手工 copy。
3. Headless Hub 允许零 Collector Instance 启动，单个 Instance 的 Package 缺失/损坏/初始化失败只影响该
   Instance。
4. Desktop Release 用产物断言与打包后的 startup smoke 把上述边界钉住。

## Acceptance

- [x] Desktop 构建不再依赖 Browser 产物：Mac/Windows head 与两个 Desktop 测试项目都不再 import
  `BrowserCollectorPackage.targets`；Desktop Release 不再装 node、不再 `npm ci/build`、不再跑
  `collector-contracts.mjs`（Browser 验证留在 `collector-contracts.yml`）。
- [x] Browser package source 缺失、为 `null`、内容损坏，或 installation ledger 损坏，都不会让组合根或 host
  启动失败，只体现为 `NotInstalled/Waiting` 或 `Degraded`。
- [x] Browser 未安装时 `hello` 返回 `package_not_installed`，只拒绝该次连接；随后导入 Package 后同一
  handler 可以正常握手。
- [x] Desktop UI 在未安装时显示明确的「未安装采集器包」，不再显示成「尚未连接浏览器」。
- [x] System Collector Package 出现在 `dotnet publish -o <dir>` 产物里，Browser 不出现。
- [x] Headless 配置 `instances: []` 或省略 `instances` 都能启动，管理面快照为空。
- [x] Headless 中一个 Instance 的 Package source 缺失或损坏时，其余 Instance 照常 Ready，坏的那个在管理面
  快照里是 `Failed` + `StatusDetail`。
- [x] Desktop Release 断言 publish 产物与最终打包产物里「System 在、Browser 不在」，并对打包后的 host 跑
  一次 `--verify-startup` smoke。

## Verification

- `dotnet test Heartbeat.slnx -c Release`：12 个测试项目全绿，1079 passed / 0 failed。
- `dotnet build Heartbeat.slnx -c Release`：0 warning / 0 error。
- `node scripts/collector-contracts.mjs check`：绿。
- publish 交付契约实测：`osx-arm64`、`win-x64`（两者均 System manifest 在、`CollectorPackages/Browser`
  不存在）。此前同一 target 也在 `win-arm64` 上验证过。
- 打包前的真实 startup smoke：`/tmp/hb-publish-mac/Heartbeat.Desktop.Mac --verify-startup=<report>` 退出码
  0，报告为 `hostStarted:true` 且 Browser 处于 `Degraded`（本机遗留了一份内容不完整的 Browser
  installation）——即「已安装但损坏」这条分支在真实产物上也不阻断启动。

## Remaining human gate

- Desktop Release 新增的断言与 smoke 只会在真实 tag push 时执行；本轮没有发 tag，因此 CI 上未跑过：
  - Windows Portable zip 的目录层级（断言已写成不依赖层级的查找）；
  - vpk 打包后的 `Heartbeat.app/Contents/MacOS/CollectorPackages/...` 路径；
  - Windows 打包产物的 `--verify-startup`（本机没有 Windows 环境，`win-arm64` 也不在 runner 上跑 smoke）。
- Browser 目前只能手工侧载：它没有独立 tag workflow，也没有可下载的 Package。这属于 issue 02/07 的范围，
  本 issue 不做。

## Comments

### 2026-09-02 — 顺手抓到的测试隔离缺陷

`AgentHostExtensionsTests` 的配置文件放在临时目录根下，而 Browser Runtime 的 state 落在 ConfigManager 的
`DataDirectory`，于是 `browser-package-state.json` 在所有测试与历史运行之间共享。新加的
`Composition_BuildsWithoutTheOptionalBrowserCollectorPackage` 第一次跑就被上一轮遗留的安装状态污染成
`IsInstalled == true`。已给该测试单独一个数据目录。**同一目录下的其他用例仍在共享临时目录根**，只是它们
不断言 Browser 状态，所以看不出来。

### 2026-09-02 — 被 issue 09 supersede

本 issue 的完成标准写成了「宿主与 Browser 解耦」，实际只做到「Browser 缺席时降级」：宿主仍持有 Browser
专属 runtime、protocol handler、安装目录默认值、平台 AppHint 知识与 UI 卡片，因此「解耦」这个说法当时
不成立。

[issue 09](./09-named-optional-collectors-out-of-host-composition.md) 把宿主组合收敛到通用 seam + System
BuiltIn，删除上述宿主特化，并按 [ADR-049](../../../docs/adr/049-named-optional-collectors-outside-host-composition.md)
记录决策与代价。上一条注释里的污染源 `browser-package-state.json` 随 Browser runtime 一起消失；宿主本机
数据树现在挂在可注入的数据目录上，startup smoke 默认落在临时的一次性目录。同一测试目录下其他用例是否
仍共享临时目录根，本轮未核查。
