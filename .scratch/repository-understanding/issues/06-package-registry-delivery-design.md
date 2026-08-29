# 06 — 设计 Collector Package 托管与下载

Status: needs-triage

Owner: Collection / Package Delivery

Priority: P2 — 当前内嵌交付可用；先形成可实施设计，不把 future seam 写成已交付能力。

## Goal

把现有本地内嵌 Collector Package 扩展为可托管、解析、下载和验证的 Artifact Delivery，保持
Collector Protocol、Execution Driver、Package/Instance/Activation 身份与 per-Instance 状态边界。

## Fixed decisions

- Registry 下载或验证失败不改写 Desired State，旧已验证 Activation 继续运行。
- 正式 Registry 中同一 `PackageId + Version` 内容不可变；同版本异 hash 是完整性错误。
- 下载、验证、Installation、Resolved State、Activation 与 Ready 是不同阶段。
- Browser staged 完成不等于更新成功；精确新制品 Activation Ready 后才更新运行事实。
- 新 PackageId 不隐式继承旧 Instance、配置、Secret 或 Stream。
- 宿主升级前对全部 Instance 做兼容预检；无共同集合时默认暂停，owner 可明确停用冲突 Collector
  后继续升级。
- Artifact Delivery 与 Execution Driver 保持正交；Package Registry 不复活旧 source-level
  Collector Registry 身份。

## Acceptance

- [ ] 建立独立 feature PRD，明确 Registry metadata、Artifact 地址/hash、下载与本地锁定状态。
- [ ] 裁决信任与发布范围：官方包、不可变发布、签名/撤回本期是否进入范围。
- [ ] 定义 install/update transaction、Last-Known-Good、离线行为与结构化冲突。
- [ ] 分别定义 BuiltIn、ManagedProcess 与 ExternalHost 的交付/激活成功判据。
- [ ] 定义宿主升级 preflight、PackageId replacement 与 config/Secret/Stream 显式迁移边界。
- [ ] 把实现拆成有 owner、依赖、自动验证和人工 smoke 的 issues；未裁决项不得伪装成字段。

## Existing design seed

- ADR-040 的 Artifact Delivery / Execution Driver 两轴、Package/Instance/Activation 身份与开发期
  同 SemVer content candidate 例外。
