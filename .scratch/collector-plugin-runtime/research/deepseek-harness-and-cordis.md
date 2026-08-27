# DeepSeek Harness 与 Cordis 对采集器运行时的启示

调查日期：2026-08-17

调查基于 DeepSeek Harness 官方仓库提交 `47f943859bef60e4160492346772ded9b24f765a`、Cordis 官方仓库与用户提供的论文。Harness README 明确采用 “everything is a plugin”、以 Cordis 为底层，并链接同一论文：[DeepSeek Harness README](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/README.md)、[Cordis](https://github.com/cordiverse/cordis)、[论文仓库](https://github.com/cordiverse/paper)。

## 可借鉴

- Package、稳定插件实例与逐次 Run/Activation 使用不同身份，并区分 current、next 与 latest attempt。[动态插件类型](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/packages/extensions/cordis-host-runner/src/types.ts#L90-L247)
- desired configuration 与实际 fiber 状态分离；配置变化由 loader 增量协调，加载失败保留 last-good 配置。[配置协调](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/vendor/loader/src/config/entry.ts#L141-L245)
- 所有上下文注册归属 fiber 生命周期；依赖消失时先撤销依赖方，再回收提供方资源。[生命周期教程](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/docs/cordis-tutorial/02-lifecycle-and-effects.md)、[fiber 实现](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/vendor/cordis/src/fiber.ts#L402-L709)
- 错误保留 activation phase 与原始 stack；测试强调真实 loader、并发竞态、错误路径和卸载清理。[测试规范](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/docs/testing.md)

## 不能照搬

- Cordis 静态插件 ABI 是同进程 TypeScript 接口，不是跨进程、跨语言 wire protocol；服务依赖靠字符串 key 与 TypeScript declaration merging。[服务模型](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/docs/cordis-tutorial/03-services.md)
- 安装型插件主要借助 pnpm、peer dependency 与 lockfile，没有 Heartbeat 需要的协议范围、平台/RID、制品签名、撤回与可信来源。[插件 CLI](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/apps/cli/src/plugin.ts)
- Context isolation 与 interception 不是恶意代码沙箱；动态 host runner 的 `node:vm` 也明确只约束诚实代码。[sandbox 限制](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/packages/extensions/cordis-host-runner/src/sandbox.ts#L1-L8)
- Effect 只能撤销被 Context 捕获的资源；已经写入外部系统或发送到网络的事实并不天然可逆。
- 动态插件 update 失败会保留旧 Package 指针，但不会自动恢复一个正在运行的旧 Run，因此“旧版本仍可选”不等于“已经回滚运行”。[版本测试](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/packages/extensions/cordis-host-runner/tests/versioning.spec.ts#L6-L39)

## 对 Heartbeat 的结论

Heartbeat 应吸收生命周期所有权、Package/Instance/Activation 身份、desired/actual 分离、阶段化错误和真实组合测试；不应把 Cordis ABI、pnpm 依赖解析或 `node:vm` 当成 Collector Protocol、供应链安全或第三方隔离方案。Collector Package Runtime 与 Collector 内部的细粒度组件运行时必须保持分层。
