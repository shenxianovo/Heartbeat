# Analytics 数据库迁移与发布

从 GitHub Actions 手动触发 **Deploy Analytics**，选择包含所需 migrations 的提交。
只触发这一个 workflow；它先测试、构建镜像，再自动迁移并启动同一镜像。
普通生产服务重启不应用 migration。决策见 [ADR-058](../adr/058-ci-database-migration.md)。

## 执行顺序

1. `image` 在 CI runner 运行 Analytics 集成测试与脚本回归，构建并输出镜像 digest。
2. `migrate` 将本次 compose 与脚本上传到 `/srv/heartbeat/releases/<run-id>-<attempt>/`。
   服务器拉取 digest 对应镜像、确认 PostgreSQL 就绪，然后停止 backend；其余 Collector 可以
   继续本地采集并保管未确认上传。新旧 Analytics 不在回填期间同时写入。
3. 在 `/srv/heartbeat/backups/analytics-<UTC>-<pid>.dump` 保存完整 custom-format 备份，
   以低压缩级别减少 1C1G CPU 负担，并用 `pg_restore --list` 检查目录可读。
   这不替代任务 05 的完整恢复、逐行数据和查询对照演练。
4. 用一次性 `backend --migrate` 容器执行 EF migrations 与两段 C# 回填，SQL timeout 为 0。
   不发布 HTTP 端口。日志保存在同名前缀 `.migration.log`；原 backend 镜像在 `.previous-image`。
   全部成功才写 `.analytics-release/ready-image`，随后一次性容器退出。
5. `deploy` 必须依赖 `migrate` 成功，核对成功凭据对应同一个 digest，再运行 `--check-database`，
   然后启动 backend。通过 Compose 内网直接检查新 Analytics 的 `/health`，最长等 30 分钟；
   容器退出或重启状态立即报错。健康后将 digest 写入服务器 `.env` 的 `BACKEND_IMAGE`，
   供后续普通 Compose 操作继续使用该版本。

workflow 全程串行，`cancel-in-progress: false`；不会因为后续发布取消当前迁移。
本流程只操作 Analytics 和所需数据库，不发布 Frontend、Hub 或 Collector。
服务器需具备当前 Compose v2、Bash 和 `flock`，沿用现有 SSH secrets 与 `.env` 数据库配置；
数据库无需向 CI 公网开放。健康的发布标记 `.success`，保留最近两份成功发布前的备份；
无成功标记的失败备份、原镜像记录和日志不会自动清理。Owner 在任务 05 核对数据和缓存恢复。

## 等待和失败

迁移 SQL 可无限等待；SSH 350 分钟、迁移 job 360 分钟仍是外围上限，部署 SSH 为 55 分钟。
设置依据：[GitHub job 超时](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idtimeout-minutes)、
[SSH action command_timeout](https://github.com/appleboy/ssh-action#-ssh-command-settings)。
普通请求的数据库超时不变。真实 SQL 错误、备份失败或镜像/版本不匹配立即失败，后续 job
不会执行。不会用忽略异常、自动 schema 降级或旧应用重启来冒充成功。

迁移失败后 backend 保持停止。即使 EF 已提交所有 migration，只要 C# 回填失败就没有成功
凭据，deploy 仍被阻止。修复原因后重新运行完整 workflow；幂等迁移和回填从当前状态继续。
失败尝试的备份保留，不能用重试后产生的较新备份替代最初升级前备份。

如果 SSH 或 CI 超时/取消，先在服务器检查，不要立刻再起一个迁移：

```sh
cd /srv/heartbeat
docker ps -a --filter name=heartbeat-analytics-migration
docker logs --tail=200 heartbeat-analytics-migration
docker compose ps --all backend
```

服务器进程锁和固定容器名会阻止重叠执行。活跃容器应继续观察；如果它已经退出但未清理，
先保存日志并核对结果，再删除该已退出容器后重试。CI 中断后即使数据库看似最新，也通过完整
迁移入口重做两段幂等回填和成功凭据，不手写凭据跳过检查。

需要恢复到旧版时，保持停写并选择**第一次失败升级之前**的备份及其 `.previous-image`。
先在隔离库验证该备份可完整恢复，再执行已核对的恢复：

```sh
set -eu
cd /srv/heartbeat
# 必须替换为已核对的升级前备份前缀；后续步骤会覆盖当前数据库。
backup_prefix='backups/analytics-YYYYMMDDTHHMMSSZ-PID'
test -s "$backup_prefix.dump"
test -s "$backup_prefix.previous-image"
docker compose stop backend
docker compose exec -T db dropdb -U heartbeat --force heartbeat
docker compose exec -T db createdb -U heartbeat -O heartbeat heartbeat
docker compose exec -T db pg_restore -U heartbeat -d heartbeat \
  --no-owner --no-acl --exit-on-error < "$backup_prefix.dump"
rm -f .analytics-release/ready-image
export BACKEND_IMAGE=$(cat "$backup_prefix.previous-image")
docker compose up -d --no-deps backend
```

恢复后核对 migration 历史、健康和代表性查询，将 `.env` 的 `BACKEND_IMAGE` 同步为已恢复镜像，
再恢复 Collector 上传。不要直接运行 `dotnet ef database update <旧版本>`：ObservationTargets
及 BrowserApplicationContexts 的 Down 明确拒绝有损降级。上述是恢复操作模板，本次未执行生产恢复。

## 对 observation-identity-targets 的影响

任务 01 的 `ObservationTargets` 和任务 02 的 `BrowserApplicationContexts` 继续追加执行，
不修改它们的编号、SQL、家族表、FactId/Revision 或缓存协议；后续 VRChat/本人关联也按此入口升级。
变化在发布编排：任务 05 必须使用候选镜像 `--migrate` 进行停写后的副本升级，再用
`--check-database` 和生产模式启动检查，保留事实身份、数量、Payload、时间、归属及缓存重放的
原有验收。迁移不能结束后先恢复旧 Analytics 写入再等待部署。

记录实际 1C1G 耗时、资源峰值和停写窗口；沿用旧脚本的历史十分钟断言不再是发布门禁。
自动测试通过不关闭任务 01–05 的真实设备、完整数据副本及生产切换验收。

本次自动验证（2026-09-10）：Analytics 全量 542 项通过；随后新增无限超时用例并重跑迁移/
命令入口 5 项通过。脚本测试 11 项通过（其中当前 Node 启动入口由其原生行为测试承接），
覆盖备份/回填失败、版本不符、遗留迁移容器、OOM/重启及备份保留。
全 solution 构建、IDE1006 命名检查、actionlint 与 shell 语法检查通过。
未执行生产部署或全量生产副本演练；任务 05 的这些验收仍未完成。
