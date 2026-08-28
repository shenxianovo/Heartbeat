# Local Data Smoke

本流程验证两件事：从服务器恢复的历史数据仍满足基本不变量，以及新启动的客户端确实让
Segment 或 InputEvent 数据水位向前推进。检查只输出聚合计数和时间水位，不读取标题、URL、
按键或账号内容。

## 1. 检查恢复后的数据

启动或刷新本地栈后运行：

```bash
node scripts/smoke-local-data.mjs check
```

检查覆盖时间范围、必填身份、外键完整性、未来时间、按 Source 的聚合计数，以及 system 重叠、
语义重复和 App 双写不一致等质量信号。完成标准是命令退出码为 0；数据集可以没有 InputEvent，
但已有记录不能违反硬不变量。质量信号会进入基线，后续客户端运行不能让它们恶化。

## 2. 记录客户端运行前的基线

```bash
node scripts/smoke-local-data.mjs baseline
```

基线写入 Git 忽略的 `.local/local-data-smoke-baseline.json`。随后启动 Desktop Agent，切换几个
前台窗口；需要验证 InputEvent 时再启用输入记录并产生输入。等待一个上传周期后运行：

```bash
node scripts/smoke-local-data.mjs verify
```

完成标准：历史 Segment/InputEvent 行数没有减少，全部不变量仍成立，质量信号没有恶化，并且
Segment 或 InputEvent 的事实时间水位至少有一个超过基线。持续中的 Segment 可能以相同 FactId
更新，因此不能只用行数增长判断成功。

## 边界

这是数据面 smoke，不替代 Dashboard 人工查看、真实浏览器扩展、系统权限或真实第三方账号
验证。真实 VRChat 账号仍按 [Headless Hub README](../../collection/hub/Heartbeat.Collection.Headless/README.md)
执行人工 smoke。
