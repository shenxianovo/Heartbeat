# macOS System 恢复验收

日期：2026-09-14

审查基线：`0bcd3b0a094b7478f7fa39b178f316b7f87b664c`

代码审查、自动回归和真实 Collector 到 Hub 的冒烟已完成。完整平台交互及 Collector 到 Web 的真机链路尚未完成。

## 验收范围

本轮恢复：

- macOS 前台应用和窗口标题；
- 锁屏、会话失活、显示器休眠、系统休眠；
- 非文本物理输入；
- 观察能力历史状态；
- Hub 批量交接和统一时间轴展示。

Collector 到 Hub 接管前的快照仍在内存，进程退出可能丢失；本轮没有增加 Collector 持久队列。结果更正只确认了 [ADR-0006](../adr/ADR-0006-result-correction-semantics.md) 的语义，尚未实现。Windows 只做能力盘点，见[平台能力对照](system-capability-inventory.md)。

## 修复结论

| 问题 | 修复与证据 |
| --- | --- |
| 事件与采样并发修改投影，事件时间被推迟 | Session 在接收时取时，单消费者串行投影；状态变化淘汰过期快照 |
| macOS uptime 不包含系统休眠 | 使用包含休眠的 `CLOCK_MONOTONIC_RAW`；覆盖休眠、唤醒和系统改钟 |
| 高频提交在锁内重复序列化 | 锁内只复制快照，分组和计量移到锁外 |
| `flagsChanged` 未覆盖左右修饰键 | 补齐原生事件和物理状态位；CapsLock 只接受 stateless 物理位 |
| AX 无值、错误和恢复状态混淆 | 正常无值与读取失败分开；附着并成功读取后才报告恢复 |
| 短区间被画得过宽，重叠区间互相遮挡 | 按真实比例绘制最小可见标记，重叠 Range 自动分行 |
| 来源筛选残留旧详情且不能组合选择 | 详情从当前数据派生，来源支持多选和空选择 |
| 协议摘要与详情分别解析 value | renderer registry 统一注册解析、摘要和详情 |

这些修复没有引入旧 Runtime、通用采集框架或业务协议到 Hub/后端。Point 计数只表示已存 Record 数量，不自动等同业务统计。

## 已执行验证

2026-09-14 执行并通过：

| 检查 | 范围 |
| --- | --- |
| `dotnet test Heartbeat.slnx --no-restore --verbosity minimal` | Domain、desktop、Hub 和 PostgreSQL 集成 |
| Web `npm run verify` | 类型、Lint、格式、Vitest 和生产构建 |
| Web `npm run test:e2e` | fixture 认证与 API 下的 Chromium 交互 |
| 真实 macOS Collector 到临时 Hub | 1 秒间隔运行 8 秒，Hub 接管 1 条应用 Record 和 3 条能力状态，无交接错误 |
| Hub 正常重启 | SQLite 中 Record 数量、ID 和内容保持一致 |

原生冒烟使用 loopback Hub、随机测试身份和临时 SQLite，认证及后端指向不可达本地端口，不访问正式服务。产物不含窗口标题，临时进程和数据已清理。它证明当次进程与 Hub 接管行为，不证明物理断电、输入完整性或整条产品链路。

## 待完成人工验收

连接隔离的 Hub 和后端，在 Web 读回同一时间窗：

1. 切换应用及同应用窗口、标题，确认应用与窗口独立分段，空白不续接。
2. 验证普通键、左右修饰键、CapsLock、鼠标按钮和双向滚动；不得保存文本。
3. 验证锁屏、显示器休眠、系统休眠及重叠原因恢复；单个原因解除不能关闭其他原因。
4. 分别撤销和授予 Accessibility、Input Monitoring；其他能力应继续工作，状态历史应与实际行为一致。

测试替身、`--once` 和上述冒烟不能代替这些步骤。

## 2026-09-20 桌面 UI 恢复验收补充

以下记录验证的是迁移前的 Avalonia 客户端，不能作为 [ADR-0017](../adr/ADR-0017-native-desktop-interfaces.md) 原生 UI 的验收证据。

比较基点：`4533ee5c50a96ebb0962aa316444ef962fd65782`。跨平台运行逻辑和 Avalonia UI、Mac 原生宿主、本机 Hub 与系统凭据保存已实现；责任和操作入口见 [ADR-0016](../adr/ADR-0016-desktop-host-and-credentials.md) 与[客户端 README](../../src/Desktop/README.md)。

| 命令 | 结果与本地证据 |
| --- | --- |
| `./scripts/heartbeat-dev env up desktop` | 成功打包并打开真实开发客户端，读取已有凭据并恢复采集；`.artifacts/desktop/dev-cli-launch-check.json` |
| `./scripts/heartbeat-dev verify changed --base HEAD` | xUnit v3 / Microsoft.Testing.Platform v2、前端检查及 fixture 浏览器验证通过；`.artifacts/verification/20260920T072856Z-verify-full-fallback-27274b641e0148e5b0a28d7fc169a5af/manifest.json` |
| `./scripts/heartbeat-dev quality --base HEAD` | 通过，无新增重复代码或复杂度热点；`.artifacts/verification/20260920T072942Z-quality-gate-01795c0419e74791ab92a7d3257dfd70/manifest.json` |
| `./scripts/heartbeat-dev scenario delivery` | MTP 类过滤和 TRX 报告通过；`.artifacts/verification/20260920T070812Z-scenario-delivery-9bd902001bda4f09be12fde0a2107a50/manifest.json` |
| `./scripts/heartbeat-dev scenario desktop-replay --keep-environment-on-failure` | 本地应用包、真实 Auth/钥匙串、原生采集、进程内 Hub、隔离后端与生产 Web 的同一 Record 核对通过；退出时队列为零，临时 profile、凭据及 Docker 环境清理成功。证据 `.artifacts/verification/20260920T072545Z-scenario-desktop-replay-af5d844ec8ee409dab93206736bfbaae/manifest.json` 与同目录 `replay-record.png` |

正常开发入口使用固定数据目录和已保存凭据；只有 `desktop-replay` 创建一次性的验收 profile。白板 app 与 `collector-replay` 已删除，真实客户端自身的前台区间进入验收链路。操作工具能点击后台控件不等于系统前台已改变，验证必须以实际 Record 为准。

这是带日期的本地验收快照，不覆盖上文完整平台交互矩阵、长期稳定性、Windows 原生宿主、发行签名或自动更新。更早的 Auth/浏览器超时并未因本次通过而被证明已修复，继续见[回放稳定性记录](../../.scratch/collector-replay-stability/issues/02-auth-and-browser-timeouts.md)。


## 2026-09-20 原生桌面迁移验证

比较基点：`a03edc5921cca2846d989a98729c78d8e7b77644`。AppKit 与 WinUI 宿主代码、共享运行模块和 Windows 观测适配已实现，原生运行尚未验收。以下命令使用已安装 macOS workload 的隔离 SDK（`PATH="$PWD/.artifacts/toolchains/dotnet:$PATH"`）。

| 命令 | 结果与本地证据 |
| --- | --- |
| `./scripts/heartbeat-dev verify changed --base a03edc59` | .NET 测试、前端检查和 fixture 浏览器检查通过；`.artifacts/verification/20260920T082745Z-verify-full-fallback-1e2cb20e34844c32805e2e401ca21dbe/manifest.json` |
| `./scripts/heartbeat-dev quality --base a03edc59` | 通过；包含两套原生宿主的托管编译和分析器，不链接原生应用；`.artifacts/verification/20260920T083217Z-quality-gate-bf4e8a46f258458ba4f9c3ad13470c57/manifest.json` 与 `quality.json` |
| `./scripts/heartbeat-dev scenario desktop-replay` | 失败于 AppKit 打包，缺少完整 Xcode；隔离环境已清理；`.artifacts/verification/20260920T081939Z-scenario-desktop-replay-9d9a0798a4104fc6b14659cca5298f25/manifest.json` 与 `desktop-build.log` |

Windows 的 WinUI 打包、系统凭据、托盘和 Win32 原生观测尚需 Windows 实机；Mac UI、应用生命周期、钥匙串与真实回放需要在 Xcode 配置完成后验收。后续步骤见[待验收记录](../../.scratch/native-desktop/issues/01-platform-acceptance.md)。本轮不以旧 Avalonia 场景或跨平台纯逻辑测试替代这些验收。
