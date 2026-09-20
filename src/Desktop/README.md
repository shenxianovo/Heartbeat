# Heartbeat 桌面客户端

桌面客户端共用 Avalonia 界面和运行逻辑，当前原生宿主为 macOS。Windows 的原生采集、凭据库接入和应用包尚未实现；共享模块不依赖 Mac 项目。

## 使用

```bash
./scripts/heartbeat-dev env up desktop
# 一并启动本地 Web、API 与数据库：
./scripts/heartbeat-dev env up web desktop
```

CLI 会调用打包脚本生成并打开 `.artifacts/desktop/Heartbeat Dev.app`，不额外启动服务端 Hub。改动代码后先退出开发版再执行命令。

可以把 `Heartbeat Dev.app` 拖入本机 Applications 目录再打开。该产物为本地 ad-hoc 签名，未做发行签名、公证或自动更新，不用于上线分发。两个平台的原始应用图标位于 `assets/desktop-collector/`；界面直接引用这些资源，Mac 打包时生成 ICNS。

本地开发包显示为 **Heartbeat Dev**，Bundle ID 为 `com.shenxianovo.heartbeat.desktop`，与旧版 `com.shenxianovo.heartbeat` 不同。开发包使用独立的 `Heartbeat/Desktop` 子目录，不替换 `~/Applications/Heartbeat.app`。

首次打开进入“连接设置”，填写后端、Auth、Web 时间线地址和 API key，点击“验证并保存”，然后开始采集。API key 通过现有 Auth 交换取得 Owner，保存到 macOS 钥匙串，配置 JSON 不包含密钥。后续启动自动恢复采集，离线时本机 Hub 可以接管，连接恢复后再上传。

正常接入不需要打开“钥匙串访问”手动配置权限。Auth 认证与本地保存是两个步骤：认证失败会展示安全的状态说明；保存失败会明确说明账号已验证但连接未保存。凭据写入在后台执行，避免系统授权窗口出现时阻塞客户端界面。系统返回 `-25293` 不能直接判定为“钥匙串未解锁”；不得自动锁定整个登录钥匙串、重置用户凭据库或回退到明文保存。

- 前台应用不需要额外授权。窗口标题和物理输入分别需要辅助功能、输入监控权限；用户点击对应设置按钮时由本进程申请系统权限，再打开系统设置。启动与后台检查不会主动弹权限提示。请为 **Heartbeat Dev** 授权，旧版的权限不会自动继承。若列表没有开发版，可点添加，选择实际运行的 `Heartbeat Dev.app`；系统若要求重新打开应用，按提示操作。
- 暂停时停止原生采集，并尝试把已有快照交给本机 Hub；Hub 继续处理已接管的数据。
- 关闭窗口只隐藏界面，菜单栏可以重新打开；菜单栏“Quit Heartbeat Dev”才退出客户端。退出不要求后端在线或网络队列排空。
- “打开时间线”使用默认浏览器打开配置的 Web，由 Web 完成登录和回放。

默认数据目录是 .NET `LocalApplicationData` 下的 `Heartbeat/Desktop`（macOS 为 `~/Library/Application Support/Heartbeat/Desktop`）。配置和 SQLite 队列在此目录；同一目录只允许一个客户端写入，绑定同一 Owner、后端和 Target。独立验证可显式指定目录：

```bash
".artifacts/desktop/Heartbeat Dev.app/Contents/MacOS/Heartbeat.Desktop.Mac" --data-directory /absolute/independent/directory
```

Mac Target 来自 `IOPlatformUUID`，供该平台 Collector 标识主要观测对象，不引入跨 Collector 的 Device Identity 解析。

## 实现

| 模块 | 责任 |
| --- | --- |
| `Heartbeat.Desktop` | 连接配置、用户数据目录、组合 Hub 与采集会话、开始/暂停/退出 |
| `Heartbeat.Desktop.UI` | Avalonia 界面、菜单栏、状态展示与用户操作 |
| `Heartbeat.Desktop.Mac` | Mac 应用入口、Target 读取、钥匙串和系统设置跳转 |
| `Heartbeat.Collector.Desktop` | 共用桌面观测与投影管线 |
| `Heartbeat.Collector.Desktop.Mac` | Mac 原生采集实现及独立命令行入口 |

客户端同一进程直接调用 Hub 的 SQLite 接管实现；服务器保留 HTTP 宿主。两者使用同一上传循环，各自直接向后端交付。不引入 main 中的旧协议、市场安装系统、版本兼容或退出事务框架。

## 验证

`./scripts/heartbeat-dev scenario desktop-replay` 运行本地打包应用、原生采集、进程内 Hub、隔离后端和真实 Web 回放。首次配置、开始/暂停/退出以及 OIDC 登录需要操作真实界面；场景会输出当前步骤和连接地址。临时 profile 与钥匙串条目在结束时清理。

`Heartbeat.Desktop.Tests` 使用受控观察源与 Auth 响应，验证界面操作连接到真实 SQLite 接管、凭据不落配置文件，以及暂停时交接未确认快照；这些是组合测试，不是原生端到端证据。证据边界见[工程验证](../../docs/verification.md)。
