# 桌面系统能力验收状态

本文汇总桌面采集与原生客户端已经留下的验收证据。自动测试、受控冒烟和真机交互分别陈述，不互相替代。

## 当前结论

| 范围 | 已有证据 | 仍未证明 |
| --- | --- | --- |
| macOS 前台应用、窗口标题、Away Signal、输入与能力状态 | 投影、时钟、权限退化和交接的自动测试；2026-09-14 真实 Collector 到临时 Hub 冒烟 | 常驻使用中的通知完整性、物理输入、锁屏、休眠及权限切换 |
| Hub 接管与恢复 | SQLite 接管、重启恢复和批量回执测试 | Collector 在 Hub 接管前的内存快照不会因崩溃丢失 |
| Collector 到后端 | `scenario collector-delivery` 提供真实 macOS 单次快照、Hub、API 与 PostgreSQL 证据 | 持续采样、平台交互和 Web 展示 |
| 桌面业务后台回归 | 2026-09-26 `runtime-replay` 正常回放与离线强杀恢复通过；[运行证据](../../.artifacts/verification/20260926T135317Z-scenario-runtime-replay-cdcbee04e9324a8b84043abc34c48280/manifest.json) | 系统观测和凭据适配受控，不证明原生 UI、系统采集、权限或 Keychain |
| AppKit 客户端到 Web | 2026-09-26 `desktop-replay --recovery` 两次真实 UI、Auth、采集、交付与回放通过，见[证据索引](business-coverage.md#运行证据) | 使用 Dev 凭据文件和浏览器短期令牌会话；不证明普通 Keychain、交互 OIDC、任意崩溃时刻或全部原生能力 |
| Windows 原生客户端 | 共享运行逻辑及托管代码检查 | WinUI、系统凭据、托盘、Win32 通知和真实输入均需 Windows 实机验收 |
| 发行 | 无 | 安装器、发行签名、公证、自动更新和长期稳定性均不在当前证据范围内 |

结果更正语义已经由 [ADR-0006](../adr/ADR-0006-result-correction-semantics.md) 确认，但尚未实现。Collector 到 Hub 接管前仍使用内存缓冲，见 [Hub 交付](../hub-record-delivery.md)。

## 已保留的历史证据

### 2026-09-14 macOS Collector 恢复

基线 `0bcd3b0a094b7478f7fa39b178f316b7f87b664c` 的 .NET、Web 和浏览器自动回归通过。真实 macOS Collector 以 1 秒间隔运行 8 秒，临时 Hub 接管了应用 Record 与能力状态；Hub 正常重启后 SQLite 中的 ID 和内容保持一致。

该冒烟使用 loopback Hub、随机测试身份和不可达的本地后端，不访问正式服务，也未保存窗口标题。它只证明当次进程到 Hub 的接管与重启恢复，不证明物理输入、断电或完整产品链路。

### 2026-09-20 原生客户端迁移

基线 `a03edc5921cca2846d989a98729c78d8e7b77644` 上，AppKit、WinUI 和共享运行模块通过托管编译、自动测试及结构质量检查。`scenario desktop-replay` 当时因机器缺少完整 Xcode 而在 AppKit 打包阶段失败。

同日更早的 `desktop-replay` 成功记录使用的是已被 [ADR-0017](../adr/ADR-0017-native-desktop-interfaces.md) 替代的 Avalonia 客户端，因此只作为历史信息，不作为当前原生 UI 证据。

## 仍需人工验收

在隔离环境运行当前原生客户端并从 Web 读回同一时间窗：

1. 切换应用及同应用窗口、标题，确认应用与窗口独立分段，Observation Gap 不被补齐。
2. 验证普通键、左右修饰键、CapsLock、鼠标按钮和双向滚动，确认不保存文本。
3. 验证锁屏、显示器休眠、系统休眠及重叠原因恢复。
4. 分别撤销和授予 Accessibility、Input Monitoring，确认其他能力继续工作且状态历史准确。
5. 验证 AppKit 与 WinUI 的连接、开始/暂停、隐藏/重开、常驻入口、退出和凭据恢复。

自动入口、环境要求和证据位置见[工程验证](../verification.md)。平台能力差异见[能力对照](system-capability-inventory.md)。
