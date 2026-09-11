# Observations 存储升级与恢复演练

2026-09-11：脚本候选目标已更新为 `20260911060000_DirectObservations`（直接 FOI/Relations），完整副本演练仍暂停；
本次未复用旧候选的成功记录作为五表验收。`--check-database` 必须使用同一候选镜像，
确认最后迁移为上述版本；旧候选的检查成功不能代替新 schema 的检查。

当前切换入口遵循 [ADR-058](../adr/058-ci-database-migration.md) 和
[Analytics CI 迁移 runbook](analytics-database-migration.md)。本文件补充任务 05 的完整数据、
缓存、查询核对和恢复步骤；存储审阅见 [实施说明](../architecture/observation-target-cutover.md)。

## 隔离完整副本演练

要求 Docker、Python 3.11+、已构建的候选镜像和 family baseline 完整 custom-format 备份。
脚本不连接备份来源、不使用日常数据库卷、不启动真实采集器，创建随机隔离网络和新容器，
退出时清理它们。原始备份、逐行内容和日志包含私有数据，只保存在忽略的 `.local/`。

```sh
docker build -f server/Dockerfile -t heartbeat-observation-cutover:five-tables .
python3 scripts/rehearse-observation-cutover.py \
  --backup .local/observation-cutover-05/production-refreshed.dump \
  --image heartbeat-observation-cutover:five-tables \
  --baseline-image heartbeat-local-backend:latest \
  --output .local/observation-cutover-05/run-NN
```

备份必须停在 `20260908141403_NativeFactCustody`，空家族拒绝作为全量演练。
脚本解析候选 tag 为 image ID，后续每个迁移、检查、Production 容器使用同一个不可变 ID。
SQL timeout=0，外围默认 21,000 秒，可显式调整；不存在十分钟成功断言。
数据库限 0.75 CPU/768 MiB，Analytics 限 0.25 CPU/256 MiB，swap=0；这是容器限制，
两者上限已经合计 1 GiB，不包含宿主 OS、Frontend、Collector，不能称为完整 1C1G 整机验收。
512 MiB 数据库恢复反复被内存限额终止；不能用提高容器预算后的成功关闭真实 1C1G 门禁。

流程：完整恢复 → 基线逐行导出 → 模拟停写/完整备份 → 注入 UPDATE 失败并证明迁移失败、
检查/Production 均拒绝 → 删除注入故障 → 同镜像 --migrate → --check-database → Production
健康 → 全部行/归属/查询对照 → 停止 Production → 幂等迁移重试/再次对照 → 从本次升级前备份
完整重建隔离库 → 启动指定的原版本镜像并核对健康 → 对照原始家族内容、查询与 migration 历史。

逐行比较覆盖 Id、Owner、Stream、FactId、Revision、Source、AppIdentityId、家族时间、Payload。
Segments 原地改为 Facts，Events 搬入后退役；脚本核对 OID、统一表行数、FOI 和精确关系完整性。
另按基线保存的证据构造每行预期归属，比较迁移后的 Collector/FOI 与精确 Fact 关系（对象 UUID 解析为
设备/App/历史账号身份后对照）。比较行数还必须等于来源 count，不能以两个空导出宣称成功。
查询对照包含逐日/设备/App 的 System 数量与时长、逐日/设备/输入编码数量；完整 API/本人关系
语义由同版本集成测试覆盖，SQL 聚合不宣称已遍历所有页面。

恢复默认会建索引。512 MiB 下曾发生维护进程被 signal 9 终止，恢复步骤对 **pg_restore
会话** 设置 `maintenance_work_mem=32MB`、`max_parallel_maintenance_workers=0`；但该设置单独并不能解决 512 MiB 下的恢复失败；最终演练提高数据库预算到 768 MiB。
这不改变普通查询/迁移预算。实际恢复命令也必须携带相同设置，不能仅记录一份未执行模板。

## 实际切换清单（部署前执行）

1. 记录已部署 Analytics/Frontend/三个 Collector 的制品版本；列出每个 Desktop Profile、
   Browser Profile 和 Headless 实例。任务 01 的 Windows/System 与 03 的真实 VRChat 仍需 Owner 验收。
2. 停止各实例后复制 Runtime JSON、collector-data 下检查点/outbox、旧 segments/input 缓存、
   dead-letter 与 Browser pending/session 备份。保留权限及目录结构，不输出凭据，不只备份空队列。
   本地实例示例位于 `.local/desktop/`、`.local/headless-data/`；实际安装从其配置确认数据目录。
3. 执行授权的 Deploy Analytics workflow：同 digest 测试/构建 → 停止旧 backend → 完整数据库备份
   → --migrate → --check-database → Production。迁移期间及新版本启动前不能恢复旧 Analytics 写入。
4. 切换当前 Dashboard，再逐个升级 Runtime/Collector/Browser Profile；先保留现场副本，再允许恢复上传。
   旧 Runtime 自动保留 `.vN.bak`，VRChat 保留 `.v1.bak/.v2.bak`。不要删除 dead-letter 伪造排空。
5. 核对三个 Collector 新事实都有 Collector/FOI。关闭窗口仍能读取旧 Browser Facts；VRChat 重启
   Collector 保持、账号切换分开，旧未知账号不变成当前登录账号。核对两个 Stream 同 FactId 仍两条。
6. 对照升级前后事实身份/数量/时间/Payload/Revision。新事实接入后按备份中的身份集合比较，
   不能再要求当前总行数等于备份总行数。设备/App/账号/本人查询依据 FOI/Relations 与明确使用者关联，
   Browser/VRChat 不计入 System 注意力；历史未知 FOI 仍可由原始 Fact/Experience 读取。
7. 记录真实停写开始、迁移结束、健康时间、整机 CPU/内存峰值、磁盘/WAL 峰值和备份留存。
   成功前不得清理升级前副本；沿 CI 规则保留最近两次成功备份和全部未解决失败备份。

## 失败恢复

迁移失败后保持停写。已应用 EF 但 C# 回填失败也不能绕过成功凭据启动；CI 脚本回归单独验证此门。
先检查固定迁移容器是否仍运行并保存日志，不并行重试。修复后完整 workflow 可重试同版本。

恢复旧版前，选择第一次升级失败之前的完整 `.dump` 与 `.previous-image`，先在隔离库恢复核验。
若新版本已接收数据，还须保管新数据库和已确认的新事实、未确认缓存；直接覆盖会丢失已 ACK 数据。
下面为经过同格式隔离恢复的命令形状，生产目标仍须按已核对的 backup_prefix 替换：

```sh
set -eu
cd /srv/heartbeat
backup_prefix='backups/analytics-YYYYMMDDTHHMMSSZ-PID'
test -s "$backup_prefix.dump"
test -s "$backup_prefix.previous-image"
docker compose stop backend
docker compose exec -T db dropdb -U heartbeat --force heartbeat
docker compose exec -T db createdb -U heartbeat -O heartbeat heartbeat
docker compose exec -T \
  -e 'PGOPTIONS=-c maintenance_work_mem=32MB -c max_parallel_maintenance_workers=0' \
  db pg_restore -U heartbeat -d heartbeat --no-owner --no-acl --exit-on-error < "$backup_prefix.dump"
rm -f .analytics-release/ready-image
export BACKEND_IMAGE=$(cat "$backup_prefix.previous-image")
docker compose up -d --no-deps backend
```

核对恢复版本 migration 历史、健康与代表性查询，将 `.env` 的 BACKEND_IMAGE 同步后才恢复相容
Collector 上传；未经核对的新版本缓存不要交给旧 Runtime 重写。禁止用 migration Down 冒充无损恢复。

## 本次结果

2026-09-11，Owner 要求暂停受限迁移，run-07 已中断并清理隔离资源。最终候选完整前后逐行
对照、同镜像重试/Production 健康和原镜像备份恢复均未完成，不能以脚本已存在宣称通过。
新生产副本为 223,313 Segments、1,939,969 Events；完整备份及失败日志留在私有输出目录。
各次失败/暂停、校验值与剩余门禁见 [脱敏报告](../../.scratch/observation-identity-targets/cutover-report.json)。

Standards / Spec 两轴审查及命令入口补审均为 0 项确认缺陷。自动测试与真实 Chrome 离线链路
见 [任务 05](../../.scratch/observation-identity-targets/issues/05-cutover-and-cleanup.md)。
本任务未授权或执行生产部署；普通资源完整副本验证是否继续以 Owner 后续选择为准。
