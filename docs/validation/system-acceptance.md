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
