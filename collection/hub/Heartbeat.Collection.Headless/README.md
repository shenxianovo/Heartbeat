# Heartbeat 无头 Hub

无头 Hub 使用与桌面 Agent 相同的鉴权、持久 Collector inbox、Segment 缓冲、缓存和上传流，
但不依赖前台窗口 API 或发布供应商。一个 Collector Runtime 托管全部已配置的 Collector
Instance；每个 Instance 拥有独立的上传身份、缓存和加密 Secret 命名空间。每个 Instance 的
投影、当前状态、缓存和最终上传由同一个 pipeline 模块负责；某个 Instance 等待登录时，不会
阻塞其他已配置的 Instance。

面向 owner 的管理 API 位于 `/hub/api/v1`。它只接受 `sub` 和 `client_id` 与本 Hub 配置匹配的
OIDC access token。Dashboard 通过同源反向代理直接调用该 API；凭据和可复用的 VRChat 会话
不会经过 Analytics。

## 构建 Collector Package

构建一个行为确定的本地参考 Package：

```bash
dotnet build ../../collectors/Heartbeat.Collector.Reference.ManagedProcess/Heartbeat.Collector.Reference.ManagedProcess.csproj
../../collectors/Heartbeat.Collector.Reference.ManagedProcess/bin/Debug/net10.0/Heartbeat.Collector.Reference.ManagedProcess \
  --create-package ./reference-package
```

构建 VRChat Package：

```bash
dotnet build ../../collectors/Heartbeat.Collector.VRChat/Heartbeat.Collector.VRChat.csproj
../../collectors/Heartbeat.Collector.VRChat/bin/Debug/net10.0/Heartbeat.Collector.VRChat \
  --create-package ./vrchat-package
```

## 配置

创建 `heartbeat-headless.json`。其中的相对路径均以该配置文件所在目录为基准解析。
`instances` 可以包含多个账号，后续也可以加入其他托管式 Account Collector Package：

```json
{
  "apiKey": "replace-me",
  "dataDirectory": "./data",
  "uploadIntervalSeconds": 60,
  "listenUrl": "http://0.0.0.0:8082",
  "management": {
    "ownerSubject": "the-owner-oidc-sub",
    "authority": "https://auth.example.com",
    "issuer": "https://auth.example.com/",
    "clientId": "heartbeat-web",
    "audience": null,
    "requireHttpsMetadata": true
  },
  "instances": [
    {
      "instanceKey": "vrchat-alice",
      "packageDirectory": "./vrchat-package",
      "subjectId": "0198d5df-5df3-70a1-937d-68a7d64623e2",
      "subjectKind": "Account",
      "subjectName": "VRChat · Alice",
      "configVersion": 1,
      "config": { "pollIntervalSeconds": 60 },
      "startupTimeoutSeconds": 30,
      "drainGraceSeconds": 10
    }
  ]
}
```

## 运行

直接运行：

```bash
dotnet run --project Heartbeat.Collection.Headless.csproj -- ./heartbeat-headless.json
```

使用 Compose 栈时，把 `heartbeat-headless.compose.example.json` 复制到
`.local/heartbeat-headless.json`，并替换 API key、owner `sub` 和 Subject ID。无头 Hub 是默认
本地栈的一部分，通过常规命令启动：

```bash
./scripts/start-local.sh
```

Compose 会构建 Hub 和随附的 VRChat Package，挂载持久化 `/data`，并加入前端网络，作为名为
`headless` 的 nginx upstream。配置文件位于其他位置时，设置 `HEADLESS_CONFIG_PATH`。生产环境的
`compose.yml` 通过同一主路径启动已发布的 Headless 镜像。

## 本地验证

本地验证摄入时，可以用 `HEARTBEAT_API_BASE_URL` 覆盖 Analytics 端点。

设置 `HEARTBEAT_VRCHAT_MOCK=1` 可使用离线流程：用户名为 `test-user`，密码为
`test-password`，验证码为 `123456`。Vite 把 `/hub` 代理到 `127.0.0.1:8082`；生产 nginx
配置则要求容器网络中的 Hub 位于 `headless:8080`。

未设置 `HEARTBEAT_VRCHAT_MOCK` 时，Package 会调用真实 VRChat API。该路径有意保留为人工
smoke test：打开 owner 的 Dashboard，在 Account Subject 下点击 **登录**，然后在浏览器中完成
凭据和验证码步骤。VRChat 没有为此集成提供受支持的 OAuth 流程，因此该 Collector 仍属于
实验性适配器，不能表述为 VRChat 官方集成。

## 停止

收到 SIGINT/SIGTERM 后，Hub 会发送 `activation.drain`，等待配置的宽限期，终止仍无响应的
子进程，然后要求对应 Instance 的 pipeline 模块执行最后一次 drain。
