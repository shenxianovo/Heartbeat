# Hub 记录交付

状态：已实现

**Collector 采集，Hub 交付，后端存储，前端展示。** 决策背景见 [ADR-0009](adr/ADR-0009-delivery-belongs-to-hub.md)。

Hub 是各类 Collector 共用的交付能力，可部署在客户端或远端服务器，见 [ADR-0014](adr/ADR-0014-hub-desktop-and-server-hosting.md)。服务器侧 Collector 可以在所有桌面客户端离线时继续采集；SQLite 接管发生在运行 Hub 的主机上。当前实现由每个 Hub 直接向后端交付，没有 Hub 间转发实现。

交付路径为 `Collector -> Hub SQLite -> 后端 PostgreSQL`。Collector 不持有后端 ID；Hub 持久接管后负责注册、Track 映射、上传和重试。

## 责任

- Collector：平台观测、连续性判断、Record 内容和稳定 ID，以及交接前快照。
- `Heartbeat.Hub.Client`：提交结构、HTTP 请求和回执核对。
- `Heartbeat.Hub`：SQLite 接管、后端身份映射、上传、重试和逐条回执；`LocalHubSubmissionClient` 供同进程 Collector 调用，HTTP 宿主调用同一接管实现。
- `Heartbeat.Hub.Host`：HTTP、配置、认证和后台上传。
- 后端：Owner 归属、公共 Record 不变量、PostgreSQL 存储和查询。

Hub 不管理 Collector 的安装、启停或升级。桌面宿主组合 Hub 和平台 Collector，见[客户端 README](../src/Desktop/README.md)。`HubDeliveryLoop` 由桌面和 HTTP 宿主共用。

## 提交接口

`POST /hub/v1/records`

```json
{
  "collector": {
    "key": "heartbeat.collector.desktop.macos",
    "target": "device-a",
    "displayName": "My Mac"
  },
  "track": {
    "type": "desktop.application.foreground",
    "version": 1,
    "timeMode": "range",
    "endMode": "explicit"
  },
  "records": [{
    "id": "019e0000-0000-7000-8000-000000000003",
    "startedAt": "2026-09-12T10:00:00Z",
    "endedAt": "2026-09-12T10:03:00Z",
    "value": {"device_id": "device-a"}
  }]
}
```

请求只携带 Collector、Track 的逻辑声明和 Record，不携带后端 ID、Owner 或 `receivedAt`。

- 稳定地址为 `key + target + type + version`；`displayName` 不参与身份。
- Hub 去除声明字符串首尾空格，已有地址的时间定义不能改变。
- Point 和 `range + next_record` 的 `endedAt` 为空；`range + explicit` 必须提供。
- `value` 可以是任意 JSON 值，但不能缺失。
- 每批 1 到 500 条 Record，请求体和规范化提交均不超过 1 MiB。
- 声明与 Record 在一个 SQLite 事务内提交。校验、冲突、容量或写入失败时不确认接管。

成功返回逐条 `accepted`：

```json
{
  "results": [{
    "index": 0,
    "id": "019e0000-0000-7000-8000-000000000003",
    "status": "accepted",
    "endedAt": "2026-09-12T10:03:00Z"
  }]
}
```

`accepted` 只表示 Hub 已持久接管该快照，不表示后端已写入。客户端必须核对每项 index、ID 和结束时间，全部匹配后才能释放对应快照。

同一 ID 重复提交时，固定字段必须一致，Explicit Range 只合并更大的 `endedAt`。JSON 对象属性顺序和等价 UTC 偏移不构成冲突；等于 `startedAt` 的 `observedAt` 规范化为空。

| HTTP 状态 | 含义 |
| --- | --- |
| `401` | 本地认证失败 |
| `400` | 提交无效 |
| `409` | 声明或 Record 冲突 |
| `503` | 容量不足或 SQLite 写入失败 |

超时、断连或无法核对的回执都不能释放快照。

## 上传与恢复

Hub 将逻辑声明和后端映射一起保存在 SQLite。没有映射时，先调用后端 Collector 注册和 Track 获取，核对身份与时间定义，再保存映射并上传 Record。首次注册会由后端自动创建 Timeline。

每轮最多选择 500 条，并按后端 1 MiB 限制分批。网络 I/O 不持有 SQLite 写事务。失败来源轮换尝试，避免一个来源阻塞全部队列。

后端逐条回执必须匹配 index 和 Record ID。成功项还要包含首次接收时间和匹配的结束位置。上传期间发生的新续期不会被旧回执清除；回执丢失时按原 ID 重传。

网络错误、`401`、`429`、`5xx` 和未知 HTTP 错误保留并重试。已知的 `invalid_record`、`conflict`、`track_not_found` 或整批永久错误会暂停对应记录。暂停项占用容量，当前没有人工重试或删除入口。

Collector 停止不影响已接管记录。Hub 不要求退出前排空网络队列，重启后继续交付。

## 持久性与容量

SQLite 使用 WAL，并为每个连接设置 `synchronous=FULL`。默认最多保存 10,000 个不同 Record，包含暂停项。容量满时，同 Record 仍可续期，但不接管新 ID，也不自动丢弃旧记录。

数据库放在本地持久磁盘的用户保护目录。凭据不写入数据库，Record 当前不加密；运行中备份必须同时处理 WAL。

Collector 到 Hub 接管前的数据不受 SQLite 保护。桌面采集使用内存缓冲；正常取消时停止观察、处理已收到的事件，并在五秒内尝试最终交接，失败会报告错误。崩溃或最终交接失败仍可能丢失未接管快照。后续设计见[未决问题](recording-open-questions.md)。

## 独立运行

| 变量 | 含义 |
| --- | --- |
| `Hub__DatabasePath` | SQLite 路径 |
| `Hub__BackendUrl` | 后端 origin |
| `Hub__OwnerId` | Owner UUID |
| `Hub__AuthUrl` | Auth origin |
| `Hub__ApiKey` | Auth API key |
| `Hub__AccessToken` | Collector 本地接入密钥，至少 32 字符 |
| `Hub__MaximumRecords` | 容量，默认 10000 |
| `Hub__UploadIntervalSeconds` | 上传间隔，默认 5 秒，范围 1 到 3600 |

取得 Owner：

```bash
export Hub__OwnerId="$(dotnet run --project src/Hub/Heartbeat.Hub.Host -- --check-auth | jq -r .ownerId)"
```

`--check-auth` 只读取 Auth 配置，不访问 SQLite 或启动 HTTP 服务。

启动：

```bash
dotnet run --project src/Hub/Heartbeat.Hub.Host
```

默认监听 `http://127.0.0.1:4318`，所有接口要求 `Authorization: Bearer <Hub__AccessToken>`。远端部署必须另行提供受保护的传输路径。

每个 SQLite 文件固定绑定一个后端 origin 和 Owner。Hub 用 API key 换取短期 JWT，并在每次后端请求前核对 UUID `sub`；交换或核对失败时不匿名上传。

`GET /hub/v1/status` 返回 pending/failed 数量，`GET /hub/v1/failures` 返回最多 500 个暂停项和错误代码。两者都需要本地密钥。

统一配置和启动方式见[本地开发](development.md)，验证入口见[工程验证](verification.md)，真机边界见[系统验收](validation/system-acceptance.md)。
