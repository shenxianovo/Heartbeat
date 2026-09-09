# Refresh Local Data from the Server

当空数据库不足以验证历史报表、Replay 或 Recap 时，用服务器快照替换本地 E2E 数据库。
这是低频且涉及私密数据的操作，不是日常启动步骤。

## 安全边界

- 需要获得复制目标数据库全部数据的授权；快照包含活动记录、浏览器元数据、账号标识和 Recap。
- 脚本通过 SSH 在服务器的 Postgres 容器内运行 `pg_dump`，不会暴露服务器的 5432 端口。
- 服务器只读；本地只替换本地 Compose 的数据库。
- 刷新只启动 PostgreSQL，恢复后打印快照中的 EF migration 历史，不运行应用迁移。
- 后端、前端和 Headless Hub 保持停止，另行运行 `start-local` 才启动本地栈。
- 临时 dump 默认删除；显式要求保留时，应按敏感数据处理。

本地后端不要直接连接生产 PostgreSQL：迁移和测试写入会直接作用于生产数据。也不要复制
Postgres 原始 volume；逻辑 dump 才能保证在线一致性和版本可移植性。

## 前置条件

- Docker Desktop 已启动，仓库根目录存在 `.env.local`。
- SSH 账号可以运行 Docker。
- 远端目录包含部署使用的 Compose 文件和环境文件。
- 只恢复和检查快照不要求 checkout 与服务器版本一致；启动应用前，应确认 checkout 不早于服务器。

## 执行

无参数运行并按提示输入 SSH destination。远端目录默认是 `/srv/heartbeat`。

PowerShell：

```powershell
./scripts/refresh-local-data.ps1
```

macOS/Linux：

```bash
./scripts/refresh-local-data.sh
```

自动化或非默认部署需要参数时，直接查看脚本当前帮助，避免在文档中维护参数副本：

```powershell
Get-Help ./scripts/refresh-local-data.ps1 -Detailed
```

```bash
./scripts/refresh-local-data.sh --help
```

macOS/Linux 脚本下载时显示已接收 MiB、平均速度和耗时。快照由远端 `pg_dump` 实时压缩，
下载前不知道总大小，因此不显示百分比或预计剩余时间；SSH 密码仍在终端交互输入。

`refresh-local-data.sh` 会暂停本地写入服务，在同一个 PostgreSQL 实例中从 `template0`
创建唯一命名的 `heartbeat_refresh_*` 临时库。恢复与 migration 历史读取完成后，
在同一事务内把原库改名为 `heartbeat_before_refresh_*`、临时库改名为 `heartbeat`。
切换成功后删除旧库；整个流程不移动数据库目录，也不启动应用服务。

中途失败或收到 Ctrl+C 时，尝试还原旧库名称，应用服务仍保持停止，避免启动时改库。
若回滚失败，会打印需要检查的数据库名称；处理前应保持应用停机。强制杀进程或断电无法
触发回滚，可在本地 PostgreSQL 的 `pg_database` 中找到暂存库与旧库。它们包含私密数据，
应在确认恢复后清理。PowerShell 脚本暂未增加进度和暂存库切换。

完成标准：恢复成功，输出快照 migration 历史，仅 PostgreSQL 运行。此时可以只读检查数据库，
本地应用尚未对快照执行迁移或写入。

检查完成后，macOS/Linux 运行 `./scripts/start-local.sh`，PowerShell 运行
`./scripts/start-local.ps1`。`start-local` 只构建并启动本地栈，不下载或恢复数据；
后端启动时会自动应用当前 checkout 的待执行迁移，Headless Hub 启动后也可能上传新数据。
因此，检查线上原始快照必须在 `start-local` 之前完成。需要保留可重复恢复的原始 dump 时，
刷新可加 `--keep-dump`（PowerShell：`-KeepDump`）。

## 恢复到空数据库

按 [Development Guide](../development.md#4-停止) 停止本地栈并删除
`.local/postgres-data`。下次启动时，后端会创建空数据库并应用全部迁移。
