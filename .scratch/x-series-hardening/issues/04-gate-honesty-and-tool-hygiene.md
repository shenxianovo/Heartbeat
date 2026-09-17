# 04 闸门口径诚实化与工具卫生

Status: `implemented (self-verified, not committed)`
覆盖候选: `X-B3`（P2）、`X-A5`（P2）、`X-C4`（P3）、`X-D4`（P3）、`X-A4`（P2）

## 问题

1. **`X-B3` jscpd 观察报告其实会挂人**：`docs/verification.md:37` 说它是观察项不进闸门，实际 `CloneDetector.cs:51,58,83-86` 会让 quality 失败，而失败原因不带诊断信息（跨基点那次就被它挂掉了一部分）。
2. **`X-A5` 63 处测试重复无人消费**：报告量到了，不进闸门，也没人看。
3. **`X-C4` `artifacts prune` 默认参数基本是空操作**：`ArtifactsCommand.cs:127-128`、`ArtifactStore.cs:62-69` 的默认阈值在当前节奏下命中不到，证据目录已 2.7G、`.local` 12G。
4. **`X-D4` 契约文档改动不触发任何检查**：`VerificationPlanning.cs:100-101,108` 的 changed-files 规则里，`docs/protocols/*`、`docs/recording-api.md`、`docs/verification.md` 改了什么检查都不跑。
5. **`X-A4` 场景层是第一层测试的子集重跑**：`ScenarioCommand.cs:37-46,48-59` 跑的就是已有测试的子集，`docs/verification.md:3` 却把它当成独立的第三层证据。

## 要做的

- **jscpd 的身份要定死一个**：参与闸门就在文档里说参与，并让失败输出可诊断（哪个文件、哪两段、多少行、跟基线比是新增还是存量）；不参与就真的不影响退出码。二选一，写清选了哪个和为什么。
- **测试重复要么进闸门要么明确容忍**：倾向按现有 ratchet 哲学加一条「不许新增测试重复簇」；如果实现成本明显偏高，就在 `docs/verification.md` 写明「测试重复显式容忍，原因是 X」并把这份报告从产物里去掉——不要留一个没人看的数字。
- **`prune` 默认值改到真的会生效**，或者让验证跑完自动收一次。顺手把现存证据目录收一遍（保留最近若干次 + 所有失败的那几次），把清理前后的占用写进 Comments。
- **契约文档进 changed-files 规则**：`docs/protocols/*`、`docs/recording-api.md`、`docs/recording-storage-model.md` 改动至少要触发对应的后端/协议测试。
- **场景层要么变真、要么降级**：本轮更现实的是降级——在 `docs/verification.md` 里把它的说法改成实际做的事（「复跑与场景相关的测试子集并留证据」），并在 issue 06 里记下「真端到端场景」是后续独立工作。若你判断能低成本让 `scenario delivery` 真起进程 + 真库跑一遍，也可以做，但别为此把 CLI 再撑大。

## 边界

- `docs/verification.md` 由 issue 02 与本 issue 共用：**同一个 agent 串行做完这两个 issue**，不要并发改。
- 别碰 `src/`。

## 验收

- `dotnet test tests/Heartbeat.Dev.Tests` 全绿，新行为有测试。
- 跑一次 `./scripts/heartbeat-dev quality`，确认 jscpd 的口径与文档一致（是闸门就要能看懂为什么挂，不是闸门就不能挂）。
- `docs/verification.md` 里关于三层验证、jscpd、测试重复、prune、触发规则的说法，逐条与代码对得上。

## Comments

### 2026-09-17 实现记录

**X-B3 jscpd 的身份：选「是闸门」。** 理由是它本来就在挂人，把一个真的能发现问题的检查降级成观察项，等于用文档去迁就一个坏的失败信息。现在生产与测试各扫一遍（strict、8 行 / 70 token），用 `--baseline-from-ref <基点提交>` 比，只有新增重复簇失败，失败信息给出两段代码的位置：

```
FAILED: 3 new tests clone cluster(s) since the base:
    tests/…/RealAuthenticationPipelineTests.cs:23-31 <-> …:39-47 (9 lines, 83 tokens, new since base)
```

以前的失败原因取的是输出最后一行，也就是 `time: 437ms`；现在「扫不了」（报告没写出来）和「有新增重复」是两种不同结果，前者引用输出里真正那条 error，后者列位置。

**X-A5 测试重复：进闸门**，不再保留那份没人消费的近似重复观察报告。当前存量 18 簇（1.92%）不追溯，只拦新增。

**X-C4 prune**：默认值改成「保留最近 10 次 + 最近 5 次失败 + 2 天内的都留」，并且每次 verify/quality/scenario 跑完自动按这套默认值收一次；没有 manifest 的运行按失败对待（中途死掉的那种最值得留）。清理前后：

- 第一次自动清理：删 38 个运行，回收 **5.01 GiB**；随后几次运行又各自回收 2.22 MiB / 490.86 MiB / 1.02 GiB，今天合计约 **6.5 GiB**。
- 现在 `.artifacts/verification` = **2.1 GiB / 63 个运行**，`./scripts/heartbeat-dev artifacts prune` 报 `Would prune 0 … 0 B`：剩下的全在保留窗口内（今天跑得多）。这是刻意的——今天的证据正是可能要看的证据；默认值的作用是不让上周的运行一直躺着。
- `.artifacts/quality-baselines` = 836 MiB，是可重建的基线工作树缓存，不在 prune 范围内，文档里说明了。
- `.local` = **12 GiB**，只做 inventory，一行没删（`inventory-local` 永远不删）。

**X-D4 契约文档触发**：`docs/protocols/*`、`docs/recording-api.md`、`docs/recording-storage-model.md`、`docs/hub-record-delivery.md` → .NET 测试；`docs/verification.md` → Developer CLI 测试；其余散文仍然不选检查，但会明说没有可执行的检查。各自有测试钉住。

**X-A4 场景层：降级，不假装。** `scenario --help`、每次运行的 manifest limitations、`docs/verification.md` 的表格都改成「复跑第一层测试的一个子集并留证据」，并写明它不比对应测试多证明任何事；只有 `native-desktop` 是没有测试替身的那一层。「真端到端场景」按 issue 04 的要求记进了 issue 06 的 Comments，本轮不做。

**验证**：`dotnet test tests/Heartbeat.Dev.Tests` → 142 passed / 0 failed；`tests/Heartbeat.Collector.Desktop.Mac.Tests` 98 passed（含 M-03b 对账测试），Domain 27、Hub 54 全绿。

`./scripts/heartbeat-dev quality --base HEAD` 目前 **exit 1**，唯一原因是他人未提交的 `tests/Heartbeat.Integration.Tests/RealAuthenticationPipelineTests.cs` 里 3 处新增测试重复（issue 01 的工作）。按约定没有碰那个文件。把它 `--ignore` 掉重扫，`newClones: 0`——本轮改动自己没有引入新的重复。这正是期望的行为：闸门指着具体位置挂人，而不是安静地绿。
