# Browser 开发 Profile 绑定与保留状态更新

Status: ready-for-human

用户要求：开发 Browser Collector 永远不向生产 Desktop 建立采集会话；开发更新保留扩展身份、配置和未上传数据，更新后重连。
用户进一步要求本次不破坏宿主解耦和插件市场。沿用 ADR-049/050/051，Host 不包含 Browser 特例；
开发包在独立 Profile 的启动前准备，复用 Installation/Runtime，不建设在线候选/LKG 更新控制面。

方案及验收边界见 [设计说明](../../docs/architecture/browser-development-binding.md)。
实施和证据统一见 [issue 01](issues/01-profile-binding-and-update.md)。旧 Browser 正式发布的实机验收 gate 不由本轮自动关闭。

实现、自动验证及 macOS/Chrome 实机验证完成。剩余 gate：在 Windows/Edge 上复验，以及配置独立 Analytics 验收库后验证真实活动到达；由具备对应环境/账号的维护者承接。
