# 主链路历史验收记录：2026-09-07—08

以下从原设计文档完整迁出，保留当时的运行标识、源码/制品身份、结果与限制；不表示当前工作树已重新验收。
机器报告使用当时工作树的本地路径，不保证其他 checkout 可访问。当前覆盖范围见
[主链路自动验收设计](../architecture/automated-verification-design.md)，操作命令见
[验证器 README](../../tools/Heartbeat.Verification/README.md)。

## 首版验证证据（2026-09-07）

- `dotnet build Heartbeat.slnx --no-restore`：通过，0 warning / 0 error。
- `dotnet test tools/Heartbeat.Verification.Tests`：9 项通过，覆盖错误/重复结果拒绝、超时证据、凭据脱敏、命令取消，以及服务提前退出后的子进程回收。
- 真实线上 Auth + 临时 PostgreSQL + 真实 Analytics/Headless/Reference：连续两次通过，退出码 0，清理通过。运行标识为 `20260907t115535-b1917f96`、`20260907t115803-c6dd802f`。
- `--fault disconnect-upload --timeout-seconds 20`：查询保持空数组，delivery 阶段超时，退出码 1，清理通过。运行标识为 `20260907t115821-854515f8`。
- 缺失配置：configuration 阶段报告 blocked，退出码 2，清理通过。运行标识为 `20260907t120105-0279675a`。
- `--keep`：链路通过后报告 retained，发送 SIGTERM 后退出码 0、清理通过。运行标识为 `20260907t120103-f42172f2`。
- 收尾核查：上述泳道均无残留 `work/`，Docker 中无验证容器，保留报告与日志未发现配置中的 API key。

上述机器相关报告位于 `.local/verification/<run-id>/report.json`，不进入版本控制。首版真实运行平台是 macOS；其他操作系统与 Desktop 链路不由这些结果推定已验收。实现未新增生产服务自检端点，也未改变 Auth 或 Collector 的生产协议。

## Desktop 验证证据（2026-09-07）

- 当前源码：`20260907t125251-8550f246`，Reference → 原生 Desktop → Analytics 通过，正常退出与清理通过。同机已安装客户端 PID 38562 继续运行；对正在验收的同一 Profile 再次启动返回 3。
- Velopack 打包、解包后的 `.app`：`20260907t130105-eec14bba`、`20260907t130335-95d11874` 两次通过，未重新构建 Desktop。后一报告记录版本 `0.0.2-verification.1+da70af86dfd5aeb6db305737035a053503ee4e55`；两次制品树 hash 相同：`sha256:c612e5c946eddf0d70d4a76fcda12a8a700c544dd9e78fa1c6d784f3f1a5d15e`。UI readiness、安装能力关闭、真实 Machine 映射、正常退出码 0 均有报告证据。
- Headless 回归：`20260907t130105-e259538e` 通过，清理通过。
- 确定性断链（TCP reset）：`20260907t130540-1a4c1e65` 的 delivery 在 90 秒后按预期失败，退出码 1；Desktop 正常退出码 0，清理通过。
- `.app` 验收前后，日常 `config.json` 与 LaunchAgent plist 的 SHA-256 指纹相同；正常运行证据无 API key 泄露，除网络黑洞失败现场外均已删除 work。
- 全量 `dotnet test Heartbeat.slnx --no-restore`：13 个测试程序集共 1182 项通过，包括验证器 19 项、共享 UI 57 项、Mac 79 项、Windows 38 项。Windows 测试在 macOS 的 .NET 运行时执行，不等于 Windows 原生设备验收。
- 原故障端点（绑定但不 listen 的 Socket）在 macOS 上造成 TCP 连接长时间等待：`20260907t130149-efa1c830` 的 delivery 超时，正常退出超过 30 秒。验证器随后强制回收进程组和数据库，保留 work 和失败日志。这个结果不计为干净退出通过，也不因普通链路成功而关闭；后续退出责任验证需覆盖长期无响应请求及最终缓存完成。当前 `disconnect-upload` 改为独占 listener 接受后立即 reset，使故障语义确定为连接失败；它不覆盖网络黑洞。

上述真实链路平台均为 macOS。`.app` 使用现有发布参数在本机打包，仅在独立目录执行，未安装或发布。Setup、真实权限、自启动注册和 vA→vB 更新仍待各自设备验收。

## P2 修复后的干净提交验收（2026-09-08）

本轮验收来源是本地分支 `codex/profile-verification-closeout` 的代码提交
`c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，Git tree 为
`23262b287a756f27e739ec69127b42faedf6a9c3`。代码先提交，再从干净工作树 publish
验证器、Analytics、Headless、Reference 和 macOS Desktop，并用现有 Velopack 参数打包、解包
到隔离目录。每条构建、打包及运行命令前检查 HEAD 和工作树，命令后记录工作树状态；全部为空。
本节属于验收完成后的独立、仅文档提交，不是被测制品的源码提交。版本与目录 hash 只是制品身份，
源码关联由上述 Git 状态和实际构建命令共同证明，不能仅从版本号推定。

机器证据根目录为本 worktree 的 `.local/p2-closeout/`，其中 `provenance.json` 保存完整命令、
工作目录、时间、退出码、前后 Git 状态及报告路径；每条命令另存同名 `.log`。
真实运行均使用独立 PostgreSQL、Analytics、Profile/数据目录和线上 Auth；没有安装、发布制品，
也没有修改主工作树或操作日常已安装客户端。

| 场景 | Run ID | 结果 / 验证器退出码 | 清理 |
| --- | --- | --- | --- |
| 当前提交源码 Desktop | `20260908t003940-57cb3593` | passed / 0 | passed |
| 打包 `.app` 第一次 | `20260908t004059-4109ad3a` | passed / 0 | passed |
| 同一 `.app` 第二次 | `20260908t004214-c9bdc6d0` | passed / 0 | passed |
| 同一 `.app` TCP reset | `20260908t004325-00487ab7` | delivery 90 秒超时，预期 failed / 1 | passed |
| 源码不可构建时使用全部已有制品运行 Headless | `20260908t004507-f36b490e` | passed / 0 | passed |

前四项报告位于 `.local/verification/<run-id>/report.json`；最后一项位于
`.local/p2-closeout/broken-source/.local/verification/<run-id>/report.json`。
四次 Desktop 均记录原生 UI ready、安装能力 detached、正常退出码 0；三次无故障运行验证
Machine subject 映射，Headless 验证 Account subject 映射。TCP reset 不表示 delivery 成功，
也不覆盖长期无响应的网络黑洞。

源码 Desktop 版本为 `1.0.0+c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，目录 hash 为
`sha256:d67bd6c731c5d3d6c113613a720cd37f207f019badfacbab3d5ccc0fb21696f6`。
三次打包制品运行使用 `.local/p2-closeout/artifacts/app/Heartbeat.app`，版本均为
`0.0.2-p2.1+c376315ae6e486ea9ddfa28c7421d1c528afdb6e`，目录 hash 均为
`sha256:78c0ce5c1346c186d53caa30d43598512431032af9c5c1319ace288f4d70e835`。
Analytics、Headless、Reference 的路径、版本及目录 hash 同样保存在 provenance 和对应报告。
源码泳道 publish 的临时制品随 work 清理；打包制品和预构建验证器保留在隔离 artifacts 目录。

最后一项从独立临时仓库启动预构建 `Heartbeat.Verification.dll`，同时显式指定
Analytics、Headless、Reference 三个制品。该仓库两个服务项目均为故意损坏的 XML；分别执行
真实 `dotnet build` 得到 `MSB4025` 和退出码 1，之后真实 Headless 链路仍通过。全部已有制品的
四次运行都没有服务 `build-*` 日志，证明制品执行入口未触发服务源码构建；验证器准备步骤仍会
构建自身源码依赖。

回归与独立审查：

- 大小写敏感 APFS 临时卷先复现原实现误判（预期 acquired，实际 occupied），修复后 Profile
  测试 9/9；普通卷最终验证器测试 22/22，含跨进程别名/旧 Mutex 保护及真实 CLI 入口边界。
  日志分别为 `profile-red.log`、`profile-sensitive-green.log`、`verification-tests.log`。
  敏感卷通过 `HEARTBEAT_PROFILE_TEST_ROOT` 指向隔离挂载点；临时卷已卸载，镜像已删除。
- 最终 solution build 为 0 warning / 0 error；IDE1006 命名检查通过，见 `final-build.log`、
  `naming.log`。Standards 与 Spec 两个独立审查均无新增 actionable finding。
- 全量测试运行时为 1182 passed / 2 failed（当时验证器 21 项；随后新增旧 Mutex 边界测试并
  单独重跑验证器至 22/22），不能标记全量通过。见 `full-tests-docker.log`：两个失败均在
  `VRChatPackageBuildScriptTests` 查找仓库根时抛出 `DirectoryNotFoundException`；其
  `Directory.Exists(".git")` 无法识别 worktree 的 `.git` 文件。该既有、范围外测试入口问题
  未在本轮修改；后续修复与回归结果见下方补记。
- `evidence-check.json` 记录对本轮 98 份日志/JSON 的程序内扫描：未发现所用 API key 或 JWT；
  五条泳道的 work、容器及记录的进程均无残留。检查不输出凭据内容。

本轮只关闭上述三个 P2：Profile 身份判断、预构建入口说明与验证、Desktop 实施状态文档。
长期无响应网络下的退出问题仍未解决；Windows 原生设备、Setup、真实权限、自启动注册、
vA→vB 更新及 CI 接入均不由本轮结果推定完成，也未扩展 P3 抽象重构。

### Worktree 测试入口修复补记（2026-09-08）

在 `9058e9a0eb2ddbe18572151663a495ddcba08951` 的独立临时 worktree 中，执行
`dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests --filter FullyQualifiedName~VRChatPackageBuildScriptTests --nologo -v minimal`，
稳定复现上述两项 `DirectoryNotFoundException`。随后仅修改测试的仓库根识别，同时接受
`.git` 目录和文件，保留构建脚本存在检查及原有安全断言。

应用修复后，在真实 worktree 和普通 checkout 分别执行
`dotnet test collection/collectors/Heartbeat.Collector.VRChat.Tests --nologo -v minimal`，
均为 21 passed / 0 failed / 0 skipped，包含原先失败的两项。临时 worktree 已清理。
此次仅回归 VRChat 测试项目，不改写上述历史全量测试结果，也不表示当前全量测试已重跑通过。
