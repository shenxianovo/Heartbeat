# 服务器 Hub

`Heartbeat.Server` 是可部署应用入口，同一进程组合 `Heartbeat.Hub.Host`、共享管理运行模块和 `Heartbeat.Collector.VRChat`。API 存储 Record 与最近 Hub 状态，Web 展示和发起在线操作。Desktop 使用自己的进程内 Hub；无需转发给服务器 Hub。

开发环境：

```sh
./scripts/setup.sh
./scripts/heartbeat-dev env up web hub
```

重写期直接更新 Initial migration，不提供旧库增量迁移。若开发库已应用旧 Initial，需要使用新的开发库或由用户确认后重建；本次验证使用隔离数据库，没有清空现有库。

登录 Web 后从页头进入 `/hubs`。服务器联络成功后出现在线节点；选择“添加 VRChat”，填写稳定的 `usr_…` 用户 ID、用户名和密码并保存。需要两步验证时填写验证码，再次保存；认证完成后点击“开始”。密码和验证码输入在提交后清空。暂停仍允许 Hub 上传已有队列。

`compose.yaml` 的 `hub` 服务使用本应用，指定 `Hub__DataDirectory=/data`。`HubLocalStorage` 统一管理该 Hub 的本地读写，`heartbeat-hub` 卷保存身份、SQLite、公开 Collector 配置与加密会话；重建容器不要删除数据卷。可以用 `VRChat__Contact` 配置 VRChat User-Agent 的联系方式。通用 `Heartbeat.Hub.Host` 仍可单独运行，仅提供无内置 Collector 的 Hub。

直接运行本应用使用与 [Hub 交付](../../docs/hub-record-delivery.md) 相同的 `Hub__AuthUrl`、`Hub__ApiKey`、`Hub__OwnerId`、`Hub__BackendUrl`、`Hub__DataDirectory` 和 `Hub__AccessToken` 配置：

```sh
dotnet run --project src/Server/Heartbeat.Server
```

Collector 实现与观测语义见 [VRChat README](../Collectors/Heartbeat.Collector.VRChat/README.md)，身份、在线操作与迁移限制见 [管理契约](../../docs/hub-management.md)。增加其他服务器 Collector 时实现小型 factory/instance 接口并在此应用注册，不向 Hub/API 添加具名分支。

验证分为运行模块及真实 SQLite/API/PostgreSQL 测试、模拟 API 的浏览器场景，以及真实账号人工验收。后者需用自己的 VRChat 账号在 Web 完成认证，进入世界，检查回放 Record，并验证暂停与服务器重启恢复；模拟验证不证明 VRChat 线上账号行为。
