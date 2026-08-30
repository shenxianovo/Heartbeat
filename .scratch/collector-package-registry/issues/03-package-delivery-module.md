# 03 — 实现 package delivery deep module 与原子安装

Status: ready-for-agent

Owner: Collection / Package Delivery

Priority: P1 — 下载、验证与安装状态必须是一个不会泄漏部分事务的深模块。

## What to build

实现内部 `ICollectorPackageDelivery`，以 requirements 输入，隐藏 Registry refresh、SemVer/compatibility
求解、下载、验证、content-addressed staging、原子 commit 与 offline fallback。模块返回完整 resolved
结果或结构化失败，不激活 Collector、不改 owner Desired State。

## Acceptance

- [ ] `PrepareAsync(requirements)` 对全部请求生成精确且兼容的 resolved set；无共同解时返回包含每个
  requirement 原因的冲突，不留下部分 Installation。
- [ ] `OpenInstalledAsync(packageRef)` 只打开已完整验证并原子提交的 exact PackageId/version/hash；
  staging/crash/取消后不会被枚举为 Installation。
- [ ] channel/release 签名、artifact length/hash 与内部 Package manifest/artifacts/schema/declarations
  全部验证后才 commit。
- [ ] 同 hash 并发下载去重；不同 Instance 可以共享只读 Installation，但 Desired、approval、Activation
  与 LKG 不共享。
- [ ] Registry 不可达时可使用满足当前 exact ref 的已验证本地 Installation；不得凭陈旧 channel 猜测
  新版本或改写 Desired State。
- [ ] 磁盘不足、断流、超时、取消、bad signature/hash、unsupported host/protocol、same-version mutation
  都有稳定错误并保留旧 Installation/LKG。
- [ ] content-addressed cache 清理只删除无引用且非当前/LKG/staged 的内容；测试证明不会删活跃依赖。
- [ ] 单元、故障注入、并发与 restart recovery tests 通过。

## Dependencies

依赖 issue 01 的 contract 与 verifier fixtures。
