# 02 结构闸门要度量存量

> 历史命令中的启动脚本已删除；当前从仓库根目录使用 `dotnet run --project tools/Heartbeat.Dev -- <子命令>`。


Status: `implemented (self-verified, not committed)`
覆盖候选: `X-A1`（P1）、`X-C3`（P2）、`X-C5`（P3）、`X-C2`（P2）

## 问题

`.artifacts/verification/` 里 38 次 quality 运行有 37 次 `--base HEAD`。这个基点下闸门只看本次改动，已提交的债务一律不算。唯一一次跨基点度量 `20260917T041123Z-quality-git-delta-…` 直接失败：

- `main` 的生产 LOC 被算成 0（`main@86911e7` 是 `docs: archive`，源码已经不在原路径上——所以 **`--base main` 本身就是个错的基点**，工具却没告诉任何人）
- 复杂度扫描报 `CA1502 produced no production function metrics`
- 重复检测把新实现全量当成新增

结论是：`erosion 40.11% / -3.31%` 这类数字从来没有在真正的基线上被度量过。

`SourceMetrics.cs` 的兜底分类把 `src` 之外一切归成 tooling，Dockerfile 与 MSBuild 计入生产 LOC，所有 LOC 与 erosion 数字都带这层误差。

## 要做的

1. **基点选错要给能行动的诊断。** 基线端产不出生产代码指标时，不要静默出 0、也不要只说 `CA1502 produced no production function metrics`，要直说「基点 `<ref>` 没有可度量的生产代码，八成选错了基点」，并给出建议（用重写谱系内的锚点，而不是 `main`）。
2. **给这条谱系一个能长期用的锚点。** 用 tag 或在 `docs/verification.md` 里固定一个 anchor commit，让「对存量度量一次」成为可重复动作。锚点定在重写谱系里第一个有完整结构的提交上（自己判断，写清理由）。
3. **让跨基点度量跑得动。** `ComplexityDetector.cs:42-53,170-172,323` 的临时 worktree + `npm ci` 是历史失败的大头。做基线产物缓存复用（按 commit 缓存，命中就不重装），或者其它能把耗时和脆性降下来的办法。
4. **`SourceMetrics` 的分类规则显式化。** 明确哪些扩展名/路径算生产、测试、工具，Dockerfile 与构建脚本不再计入生产 LOC；兜底分支要么去掉，要么把归类不确定的单列一类。
5. **`X-C2`：至少让存量热点不是纯观察。** 12 个复杂度热点当前可以长期驻留。给一个绝对上限或收敛台账（二选一，写清选了哪个、为什么）。不要为了达标去改生产代码——本轮只立规则和度量。

## 验收

- 跑一次 `./scripts/heartbeat-dev quality --base main`：不再给出全 0 的假结果，而是明确报「基点不可用 + 建议」。
- 跑一次对新锚点的 `quality`：跑通，产出真实存量数字，证据目录留档，把数字写进 issue 的 Comments。
- `dotnet test tests/Heartbeat.Dev.Tests` 全绿，新增分类规则与诊断路径有测试。
- `docs/verification.md` 同步更新（这个文件本 issue 独占，别人不动）。

## Comments

### 2026-09-17 实现记录

**基点（X-A1）**：`quality` 先判断基点能不能用。基线树里 `src/` 下没有一个生产文件时什么都不扫，直接非零退出，并说出这棵树的代码实际在哪些顶层目录 + 该用哪条命令。实测：

```
$ ./scripts/heartbeat-dev quality --base main            # exit 1
Unusable quality base 'main'
  Git base 'main' (86911e7) has no measurable production code: 0 production files under src/,
  out of 1119 recognized files. This is almost certainly the wrong base, not a project without code.
  - That tree keeps code under .github/, .scratch/, collection/, frontend/, scripts/, server/,
    shared/, tools/; this repository measures production code under src/, ...
  - To measure stock, use the rewrite anchor 4e15d57 (feat(recording): implement timeline storage
    model): ./scripts/heartbeat-dev quality --base anchor --stock
  - To gate a change, use the commit you started from (--base HEAD for uncommitted work).
  Nothing was scanned: measuring against this base would only produce zeros.
```

**锚点（X-C3）**：`--base anchor` → `4e15d57beea301c963951fdcef4c9b5a9264212f`（`feat(recording): implement timeline storage model`）。理由：它是净室重写里第一个具备完整结构（`Heartbeat.slnx` + `src/` + `tests/` + `docs/`）的提交，在它之前只有空提交和 agent skills；在它上面 `dotnet build` 能产出 CA1502 诊断（实测 192 条），所以是**可度量**的基点，不是随手挑的一个 SHA。没有打 tag：别名写在 `QualityBase.cs` 里，`docs/verification.md` 记了理由，避免再多一个可以被移动的引用。

**跨基点可跑（X-C5）**：基线工作树按提交缓存在 `.artifacts/quality-baselines/<commit>`，复用它的 `obj/`、`bin/`、`node_modules`，只在 eslint 缺失时才 `npm ci`。首次约几分钟，第二次起本机约 **31 秒**。顺带修掉一个会安静说假话的缺陷：缓存键原来用的是 `--base` 给的字符串，`HEAD` 这类会移动的引用在 HEAD 前进后仍会命中旧目录；现在统一先 `rev-parse` 成 40 位提交号，三次扫描（LOC / jscpd / 复杂度）都用同一个提交号，并清掉了残留的 `.artifacts/quality-baselines/HEAD` worktree。

**分类显式化（X-C2 前置）**：`SourceMetrics` 的兜底分支去掉了。顺序为 生成物 → 测试 → 文档 → 构建描述 → `src/` → 工具根 → 未归类。`*.csproj`/`*.props`/`*.slnx`/`Dockerfile`/compose 与 workflow YAML/前端 `*.config.*` 归 `Build`，不再计入生产 LOC；认得出语言但不在任何已声明根下的文件归 `Unclassified`，按路径逐个列在报告里。

**存量热点（X-C2）**：选了「绝对上限」而不是收敛台账。台账要人维护、会腐烂；上限是一个数字，`tools/Heartbeat.Dev/quality-budget.json` 里的 `maxProductionComplexityHotspots`，与基点无关，每次运行都校验，只准往下改——往上改要动这个文件、出现在 diff 里、并在文件里写理由。当前值 = 实测值 12，没有为了达标改任何生产代码。预算文件缺失/读不出/负数都算失败，不会变成「没有这项检查」。

**第一次真实存量**（`./scripts/heartbeat-dev quality --base anchor --stock`，exit 0，证据 `.artifacts/verification/20260917T091513Z-quality-stock-*`）：

| 指标 | 锚点 4e15d57 | 当前 |
| --- | --- | --- |
| 生产 LOC | 609 | 11,564（C# 6,181 / TypeScript 3,636 / CSS 1,747） |
| 测试 LOC | — | 8,780 |
| 工具 LOC | — | 4,362 |
| 构建描述 LOC | — | 443（原先被算进生产语料的那部分） |
| 生产重复 | 0 | 14 簇，重复行 1.05% |
| 测试重复 | 0 | 18 簇，重复行 1.92% |
| 复杂度 > 10 的生产函数 | 0 | 12（预算 12） |
| Erosion | 0% | 40.11%（12/910 高风险函数） |
| CA1506 耦合热点 | 0 | 2 |

对比一下之前那次跨基点运行给出的东西：`baseProductionLines: 0`、复杂度 `unavailable`、重复全量新增。同一个仓库，换成可用基点之后数字才第一次有意义。

**测试**：`dotnet test tests/Heartbeat.Dev.Tests` → **142 passed / 0 failed**（改动前 85）。新增覆盖：基点可用性诊断（含「代码在 collection/, frontend/, server/ 下」这类建议）、`anchor` 别名解析、Build/Unclassified 归类与快照统计、存量预算的 4 种结果、jscpd 报告解析与失败诊断、闸门 vs 存量模式下谁挂人。

**边界**：没有改 `src/`；没有 commit/stash/checkout。工作树里有他人未提交改动，`quality --base HEAD` 因此当前是红的，见 issue 04 的记录。
