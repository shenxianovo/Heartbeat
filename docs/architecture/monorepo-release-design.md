# Monorepo 版本与发布设计讨论

状态：需求澄清中，尚未形成实施方案。2026-09-07 更新。

## 已确认的需求

- 验证与发布分别决策：PR / 主分支验证按影响范围自动运行；发布可以按发布单元选择。
- 普通改动独立发布；破坏性变更允许协调升级，当前不承诺长期维护多套旧组件版本。
- 各发布单元使用自己的 GitHub Actions 原生手动入口，不建设统一发布入口或引入自动版本 PR 工具。
- Dashboard、Analytics、Headless Hub 也需要各自可读的 `X.Y.Z` 版本；仅以提交 SHA 标识不满足需求。
  Desktop 与每个非 BuiltIn Collector 继续独立版本化，不要求各发布单元的版本号对齐。
- 首要约束是维护成本低，避免引入需要持续投入的新发布系统。

## 既有边界与当前实现

[ADR-048](../adr/048-shared-collector-host-runtime-and-independent-release-units.md)
已明确 Desktop、Dashboard、Analytics、Headless Hub 和非 BuiltIn Collector 独立发布；System
Collector 随 Desktop 发布。独立发布不意味着每个源代码项目都需要自己的对外版本号。

- [CI](../../.github/workflows/collector-contracts.yml) 在 PR 和 main push 时运行，目前没有按路径筛选 job。
- [Dashboard](../../.github/workflows/deploy-frontend.yml)、
  [Analytics](../../.github/workflows/deploy-backend.yml)、
  [Headless Hub](../../.github/workflows/deploy-hub.yml) 已有各自的手动 workflow；镜像同时标记
  `latest` 与 `sha-<commit>`，部署命令经 Compose 拉取镜像。
- [Desktop](../../.github/workflows/release-desktop.yml) 使用 `v*` tag，tag 版本同时用于各平台产物。
- [Browser](../../.github/workflows/release-collector-browser.yml) 与
  [VRChat](../../.github/workflows/release-collector-vrchat.yml) 分别使用
  `collector-browser/vX.Y.Z` 与 `collector-vrchat/vX.Y.Z`，目前接受稳定语义版本。

## 影响方案的现有约束

- [Compose](../../compose.yml) 当前引用 `latest`；虽然保存了 SHA 镜像标签，实际部署仍需进一步绑定精确产物。
- Dashboard 下载入口使用全仓 GitHub `releases/latest/download`。若为其他单元创建 GitHub Release，
  需要避免改变 Desktop 下载入口的目标；仅增加手动 workflow 不要求所有产物都创建 GitHub Release。
- Desktop changelog 当前查找上一 tag 时没有按发布单元过滤，Collector tag 可能改变其比较边界。
- Shared Kernel、Collector Protocol 和 Hub Runtime 被多个单元消费；验证范围必须包含共享依赖的消费者，
  不能仅按改动所在的顶层目录决定。映射方式尚未选定。

## 已比较的入口

| 方案 | 操作体验 | 新增维护责任 |
| --- | --- | --- |
| 每个发布单元使用 GitHub Actions 原生手动入口（已选） | 选择对应 workflow，必要时填写版本 | 参数校验与现有发布步骤的连接 |
| 统一 Release workflow | 在同一入口选择发布单元与参数 | 分发逻辑、参数组合与各发布流程的连接 |
| 自动准备版本变更 PR | 审阅版本变更后合并发布 | 版本推导规则、机器人配置与异常处理 |

GitHub 原生手动入口同时支持网页和 CLI，参见
[官方文档](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)。
已选择各单元原生手动入口；是否继续支持手工 push tag、由流程生成 tag，以及 tag 的具体生命周期仍未决定。

## 下一轮决策

1. 发版时填写完整版本号，还是选择 major / minor / patch 由流程计算下一版。
2. 正式发布的代码来源：main，还是允许指定其他分支 / 历史提交。
3. 服务器组件发版后立即部署，还是先产出版本、另行选择版本部署。

以上确定后，收敛 tag 生命周期、发布失败重试、精确部署与回滚等边界。
共享依赖的影响范围依据代码引用关系设计，不要求用户手工枚举依赖。
不会把尚未选择的候选方案写成 Accepted ADR；项目领域术语仅在产生新的领域含义时更新 glossary。
