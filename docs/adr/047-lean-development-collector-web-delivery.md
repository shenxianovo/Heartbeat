---
status: deprecated
---

# ADR-047: 开发期 Collector Web Delivery 采用最小完整闭环

2026-09-01：本 ADR 对应的实现因范围扩张与 Runtime 生效包多重状态权威而撤回；它不再授权继续实现。
Collector 独立发布与 Web 托管重新进入需求澄清，在新的 ADR 冻结发布范围和 Runtime 安装边界前不得恢复本方案。

开发阶段先证明一个非 BuiltIn Collector 能独立显式发布、从 Web 安装、由 owner 批准并建立真实 Ready Activation，不同时建设生产级软件供应链。第一条纵切只覆盖 VRChat ManagedProcess：Registry 公开读取并依赖现有 HTTPS，不做 Ed25519、密钥轮换、撤回、多 channel、SemVer 求解、离线目录或 cache GC；发布记录仍绑定精确 Version、文件长度和 SHA-256，安装只要求写入独立版本目录、通过安全解压并在内容完整后写完成标记，未完成目录永远不是 Collector Installation。

owner 通过现有认证管理面批准界面上展示的精确 PackageId、Version 与 content hash，不引入 opaque offer、审批审计或重放工作流。新候选只有 Ready 后才能接管，现有 Last-Known-Good 在此之前保持可用。System 继续随 Desktop BuiltIn Delivery，Browser ExternalHost 与生产签名、跨平台矩阵、迁移和上线演练均推迟到 VRChat 纵切证明可用之后重新裁决。

这个缩减接受开发期 Registry 不能独立证明发布者身份，也不承诺断电级原子安装；它不允许跳过长度/hash、路径边界、完成标记、显式批准或真实 Ready，因为这些属于 Package 身份和运行正确性，而非可选的供应链加固。

每个 Collector 独立拥有 `/collector-registry/v1/packages/{packageId}/current.json` 与
`versions/{version}/{artifact}`；已发布 Version 不可覆盖，修复必须发新 tag。第一版只有手动
`CheckNow`，通过现有 authenticated Hub/Headless management API 查询和批准，不建设 Dashboard 页面、
后台轮询或通知。Registry 是反向代理下的独立静态目录，artifact 先上传，最后替换 `current.json`。
VRChat candidate 到达 Ready 即更新成功；Ready 前保留旧 LKG，Ready 后退出按普通运行故障处理，不新增
候选稳定事务。

`current.json` 不复制 host/protocol compatibility matrix；现有 Package loader 与 Collector Protocol 握手是
兼容性的唯一执行门禁。第一版发布当前 Headless 环境可运行的 framework-dependent VRChat zip，不迁移旧
bundled Package：旧版本继续作为 LKG，第一个 Web 版本是普通新候选。已经下载并验证的 exact ref 即使不再是
Registry current 仍可批准；下载、校验或启动失败只保存最后错误并等待下一次人工 CheckNow/Approve，不自动
重试或清除真实候选状态。
