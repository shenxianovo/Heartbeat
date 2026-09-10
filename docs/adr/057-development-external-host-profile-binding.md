# ADR-057：开发 ExternalHost 绑定到 Desktop Profile

## Status: Accepted

## Date: 2026-09-10

## Context

ADR-053 隔离了 Desktop 数据目录和安装管理能力，但 Browser 仍按端口发现协议兼容的 Host。
同一采集器包装在开发与生产 Desktop 中时，精确制品校验无法区分宿主。用户要求开发 Browser
不向生产 Desktop 建立采集会话，并在开发更新时保留扩展身份、配置和未上传数据；同时保持宿主解耦。

## Decision

- 显式开发启动必须持有独立 Desktop Profile 的目录锁，拒绝默认生产目录及其路径别名。
  Profile 持久保存不透明身份、随机连接凭据和固定 loopback 端口；端口被占时不能扫描回退。
- 开发 ExternalHost 使用 Profile 专属通用路由，每个请求均验证绑定；旧无绑定路由在该开发 Host 中拒绝。
  生产 Host 不增加具名 Collector 分支，也不接受开发路由。
- 开发扩展的构建模式不能由 options/storage 切成生产模式。绑定缺失、Profile 不符、旧缓存端口及
  文件更新但已加载代码未 Reload 时保持离线，保留 outbox；HTTP 重定向不能绕开固定目标。
- 开发更新先完整构建/校验本地候选，再正常停止本次命令拥有的开发 Desktop。持有 Profile 锁后，
  复用不可变 Installation 和 Runtime 的启动前准备入口，保留原 Instance/config/Stream/交付状态，
  只选择精确 Package 引用。Runtime 一旦开始过 Activation，就拒绝此准备操作。
- 完成准备后更新固定扩展目录，再启动开发 Desktop。开发扩展约 30 秒检查一次本地构建标识，
  只对已固定的同一 Profile，在串行事件队列中将当前活动持久化后调用官方 runtime.reload。
  持久化失败不 Reload，每个构建最多自动尝试一次。生产扩展不启用；已有旧开发扩展需手动 Reload
  一次以加载此能力，不使用卸载重装。
  不要求 Desktop 和扩展跨进程同时切换；中间不匹配时离线等待。
- 本地连接文件与公开制品分开，凭据不进入公开 Package、源码或日志；扩展使用固定公开 key 保持
  开发身份。不同 Profile 的待传数据不自动迁移，恢复目录交换现场也必须检查原绑定。

## Consequences

通用接入和 Instance 权威仍归 Host Runtime，Browser 知识只留在 Collector 与开发工具中。
独立目录、端口和凭据解决同机误连，不承诺对控制本机账户的恶意进程提供隔离，也不能阻止端口复用后
一次失败的 TCP 探测；保证的是不向错误 Profile 建立采集会话或交付事实。

开发更新允许短暂离线，并保留旧不可变 Installation 供显式恢复；不建设在线热切换、自动回滚、候选/LKG
控制面、Store 发布或通用 Package 脚本执行器。日常生产 Marketplace 的首次安装/卸载边界不变。

## Amends

- [ADR-051 §5](051-generic-external-host-identity-and-browser-delivery.md)：只为上述独立开发 Profile
  增加保留身份的本地启动前包选择；生产 Package 更新仍未实现。
- [ADR-053](053-desktop-profile-and-installation-ownership.md)：目录所有权之外，显式开发 Profile
  增加通用 ExternalHost 连接绑定；不改变 Machine 身份或安装管理能力。

入口、验证及剩余平台验收见 [Browser 开发泳道](../architecture/browser-development-binding.md)。
