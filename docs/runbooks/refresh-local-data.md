# Refresh Local Data from the Server

当空数据库不足以验证历史报表、Replay 或 Recap 时，用服务器快照替换本地 E2E 数据库。
这是低频且涉及私密数据的操作，不是日常启动步骤。

## 安全边界

- 需要获得复制目标数据库全部数据的授权；快照包含活动记录、浏览器元数据、账号标识和 Recap。
- 脚本通过 SSH 在服务器的 Postgres 容器内运行 `pg_dump`，不会暴露服务器的 5432 端口。
- 服务器只读；本地只替换当前仓库的 `.local/postgres-data`。
- 脚本恢复后检查 EF migration 兼容性，再启动本地应用。
- 临时 dump 默认删除；显式要求保留时，应按敏感数据处理。

本地后端不要直接连接生产 PostgreSQL：迁移和测试写入会直接作用于生产数据。也不要复制
Postgres 原始 volume；逻辑 dump 才能保证在线一致性和版本可移植性。

## 前置条件

- Docker Desktop 已启动，仓库根目录存在 `.env.local`。
- SSH 账号可以运行 Docker。
- 远端目录包含部署使用的 Compose 文件和环境文件。
- 本地 checkout 不早于已部署服务器；脚本会在启动应用前验证 migration ID。

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

完成标准：脚本成功完成 migration 兼容性检查，本地栈重新启动，且
<http://localhost:8080> 能读取恢复后的历史数据。

## 恢复到空数据库

按 [Development Guide](../development.md#5-停止或重置) 停止本地栈并删除
`.local/postgres-data`。下次启动时，后端会创建空数据库并应用全部迁移。
