# ADR-0016：桌面宿主组合采集与 Hub，凭据交给系统保存

## 状态：已接受

Avalonia 的 UI 选择已由 [ADR-0017](ADR-0017-native-desktop-interfaces.md) 替代；其余运行与凭据职责继续有效。

## 日期：2026-09-20

用户确认沿用 main 的跨平台组合方向：客户端在同一进程内运行共享运行逻辑、Hub、平台 Collector 和 Avalonia，关闭窗口后继续在菜单栏运行，明确退出时停止。用户同时要求先完成功能，不带入 main 中写死的采集协议与严格生命周期框架。

桌面运行模块负责组合和启停，UI 只发起操作、展示状态。平台实现提供原生采集、Target 读取、系统权限入口及凭据库适配；Hub 保持对具名 Collector 和桌面 UI 无依赖。客户端通过进程内调用获得 SQLite 接管，服务器继续通过 HTTP 宿主接管；两处 Hub 使用相同交付实现，各自直连后端，见 [ADR-0014](ADR-0014-hub-desktop-and-server-hosting.md)。

客户端配置和队列由其用户数据目录保存，API key 的持久权威为系统凭据库；Mac 使用钥匙串。Owner 从现有 Auth 令牌交换取得，SQLite 继续约束后端与 Owner 绑定，UI 不预注册 Collector 或 Track。

暂停后 Hub 可以继续交付，退出不等待网络队列排空；已接管数据在下次启动后继续交付。会话取消时对已产生的快照做一次有期限的最终交接；这不保证进程崩溃前未接管数据的持久性，也不阻止用户退出。

本轮提供 Mac 本地应用包，回放继续由 Web 承载；自动更新、Windows 原生宿主和线上分发后续单独实现。使用和验证见[客户端 README](../../src/Desktop/README.md)。
