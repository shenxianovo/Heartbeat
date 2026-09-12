# ADR-0002：持续状态只延长已确认区间

## 状态：已接受

## 日期：2026-09-12

设计已确认，尚未提交；续期写入与重放待实现。

## 背景

持续状态需要定期确认，又不希望为每次确认保存独立 Record。网络可能中断，请求可能重复、乱序或延迟到达；设备系统时间也可能发生跳变。

曾考虑每次确认追加 Record，以及为通用快照增加版本与永久结束标记。前者增加相同状态的记录量；后者承担了当前并不需要的任意修订与永久封存规则。正常采集没有缩短已确认区间的需求，因此可以限制普通更新为区间增长，直接减少冲突处理。

## 决策

持续状态选择 `Range + Explicit`：固定同一 Record 的身份、起点和观测值，仅将 `ended_at` 原子合并为已有值与收到值的最大值。不新增 `revision`、`is_final` 或 Record TTL 字段。

TTL 根据 Collector 上传间隔设置，只判断是否缺少及时确认，不修改历史区间或永久封存 Record。超时后显示未知，有效补传仍可延长原区间。实际断采后创建新 Record，不凭相同值连接两段。

Collector 使用系统时间建立基准，使用单调时钟计算连续采集中的经过时长；续期必须有实际观测作为依据。该方法不能修复错误的初始绝对时间，不承诺自动校正历史位置。

本决定调整存储规范中“所有 Record 创建后不可修改”的规则，不改变 ADR-0001 的四层记录模型。它只支持持续状态的区间增长，不支持普通上传缩短区间或替换观测值。历史纠错、删除后防止旧重传恢复记录，以及时钟异常处理仍需单独设计。TTL 的具体算法和配置尚未确定。

## 后果

- ✅ 不必为每次持续确认新增 Record，也不必增加版本或永久结束字段。
- ✅ 合法区间更新无论重复、乱序或延迟到达，原子取最大值都得到相同结果。
- ✅ 超时不会阻止有效补传，真实断采的空白也不会仅因值相同而被连接。
- ⚠️ 普通更新不能缩短区间或替换观测值；历史纠错与删除需要单独设计。
- ⚠️ 单调时钟不能纠正错误的初始绝对时间；设备对时和时钟异常处理仍有独立成本。
- ⚠️ TTL 只判断是否缺少及时确认，不能证明 Collector 已停止；配置和具体算法尚未确定。

## 参考

- [ADR-0001](ADR-0001-time-ordered-observation-tracks.md) — 四层记录模型。
- [存储规范](../recording-storage-model.md) — 完整续期规则和未决事项。
- [Record.cs](../../src/Backend/Heartbeat.Domain/Recording/Record.cs) — 现有字段与创建校验。
- [CRDT 原始论文](https://perso.lip6.fr/Marc.Shapiro/papers/2011/CRDTs_SSS-2011.pdf) — 可交换、幂等且可结合的合并原则；这里仅借用其原则。
- [AWS 幂等接口实践](https://aws.amazon.com/builders-library/making-retries-safe-with-idempotent-APIs/) — 稳定身份、重试与原子提交。
- [.NET TimeProvider](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview) — 系统时间与时间间隔测量的区分。
