# 记录 HTTP 接口

状态：Collector 注册、Track 获取、批量 Record 上传和 Track 级重放查询已实现。

本文档记录长期有效的 HTTP 契约。一次性实现规格完成后不保留在 `.scratch`，必要信息应沉淀到本文、存储规范、协议文档或 ADR。

写入侧的调用方是 Hub：Collector 向 [Hub 提交逻辑声明和 Record](hub-record-delivery.md)，由 Hub 注册 Collector、获取 Track 并上传。Collector 不直接调用本文的后端写入接口。

## 认证

Owner 从已验证令牌的 `sub` claim 取得，必须是 UUID。客户端不能提交 Owner、Timeline、服务端接收时间或其他服务端字段。

认证复用双 Bearer 方案：`TokenSelector` 根据 JWT header 的 `typ` 选择 OIDC access token 或 agent session token；选中的 JwtBearer scheme 必须完成验签。缺失或非法 `sub` 返回 `401`。

## Collector 注册

`POST /api/v1/collectors`

请求：

```json
{
  "key": "heartbeat.collector.desktop.macos",
  "target": "device-a",
  "displayName": "My Mac"
}
```

- Timeline 必须预先存在；注册 Collector 不隐式创建 Timeline。
- `(timeline_id, key, target)` 是稳定地址。地址不存在时创建 Collector，已存在时返回原 Collector。
- 重复注册可以更新 `displayName`；并发更新以最后成功提交的值为准。
- `key`、`target` 和 `displayName` 去除首尾空格后不能为空，长度不超过 255。
- `key` 使用小写点号分段且不包含版本，例如 `heartbeat.collector.desktop.macos`。
- 成功始终返回 `200 OK`，不暴露本次是创建还是复用。

响应：

```json
{
  "id": "019e0000-0000-7000-8000-000000000001",
  "key": "heartbeat.collector.desktop.macos",
  "target": "device-a",
  "displayName": "My Mac",
  "createdAt": "2026-09-12T10:00:00Z"
}
```

错误：未认证返回 `401`；字段缺失、格式或长度无效返回 `400`；Owner 尚无 Timeline 返回 `409 timeline_not_provisioned`。

## Track 获取

`POST /api/v1/collectors/{collectorId}/tracks`

请求：

```json
{
  "type": "desktop.application.foreground",
  "version": 1,
  "timeMode": "range",
  "endMode": "explicit"
}
```

- Collector 必须存在且属于当前 Owner；缺失和不属于该 Owner 均返回 `404 collector_not_found`。
- 调用方提供 `type`、正整数 `version` 和时间定义。`timeMode` 为 `point` 或 `range`；Point 的 `endMode` 必须为空，Range 的 `endMode` 必须为 `explicit` 或 `next_record`。
- `(collector_id, type, version)` 是唯一地址。重复和并发获取复用已有 Track。
- 后端不注册或识别具体 `(type, version)`，未知类型和版本也能创建 Track。
- 已有 Track 的时间定义与本次请求不一致时返回 `409 track_definition_conflict`，且不改写已有 Track。

响应：

```json
{
  "id": "019e0000-0000-7000-8000-000000000002",
  "collectorId": "019e0000-0000-7000-8000-000000000001",
  "type": "desktop.application.foreground",
  "version": 1,
  "timeMode": "range",
  "endMode": "explicit",
  "createdAt": "2026-09-12T10:00:00Z"
}
```

## 批量 Record 上传

`POST /api/v1/tracks/{trackId}/records`

请求：

```json
{
  "records": [
    {
      "id": "019e0000-0000-7000-8000-000000000003",
      "startedAt": "2026-09-12T10:00:00Z",
      "endedAt": "2026-09-12T10:01:00Z",
      "value": {
        "device_id": "device-a",
        "application": {
          "platform": "macos",
          "id_kind": "bundle_id",
          "id": "com.google.Chrome"
        }
      }
    }
  ]
}
```

- 一批属于同一个既有 Track，包含 1 到 500 条 Record。
- 每项只接受 `id`、`startedAt`、`endedAt`、可选 `observedAt` 和 `value`。
- `id` 必须是 Collector 生成的 UUID v7。同一 Record 的续期和上传重试必须复用原 ID。
- `observedAt` 省略或为 null 时表示与 `startedAt` 相同。
- 服务端每批读取一次接收时间，用于该批中新 Record 的 `received_at`；重试与续期保留已存的首次接收时间。
- `value` 可以是任意已定义的 JSON 值；后端不按 Track 的 `type` 或 `version` 校验其具体结构。
- Point 与 `range + next_record` 的 `endedAt` 必须为空，重复上传保持原记录；`range + explicit` 必须提供 `endedAt`，并允许同一 Record 单调延长结束时间。
- Owner 缺失或不属于该 Owner 的 Track 统一返回 `404 track_not_found`。
- JSON 解析失败、DTO 字段类型错误、未知请求字段、空批次或超过上限，在逐条写入前拒绝整批。

响应：

```json
{
  "results": [
    {
      "index": 0,
      "id": "019e0000-0000-7000-8000-000000000003",
      "status": "stored",
      "endedAt": "2026-09-12T10:01:00Z",
      "receivedAt": "2026-09-12T10:01:05Z",
      "detail": null
    }
  ]
}
```

逐条状态：

| 状态 | 含义 |
| --- | --- |
| `stored` | 写入成功；首次写入、重复上传和续期共用此状态。 |
| `invalid_record` | ID、时间、Track 时间定义或缺失的 JSON value 无效；未写入。 |
| `conflict` | 同一个 ID 的 Track、开始时间、观察时间、value 或结束时间形状与已存记录不同；未写入。 |
| `track_not_found` | 批次读取 Track 后，存储入口已无法找到归属当前 Owner 的 Track。 |

HTTP `200` 不代表全部成功，客户端必须检查每项状态。同一 ID 可以在同一批出现多次，按顺序独立确认。未预期的基础设施失败可能发生在部分条目提交之后，重试必须复用原 Record ID。

Hub 只能用 `stored` 回执确认对应项的上传进度。旧请求回执只确认发送时的快照，不能清除同一 Record 后来产生、尚未确认的新进度。Collector 使用的是 Hub 的 `accepted` 持久接管回执。

## Track 级重放查询

`GET /api/v1/tracks/{trackId}/records`

查询参数：

- `from`：可选 UTC 时刻。
- `to`：可选 UTC 时刻；同时提供 `from` 和 `to` 时必须满足 `from < to`。
- `limit`：可选，默认 200，范围 1 到 500。

契约：

- Owner 缺失或不属于该 Owner 的 Track 统一返回 `404 track_not_found`。
- 时间窗按 `[from, to)` 处理。
- 有 `endedAt` 的 Record 使用区间交叠判断；没有 `endedAt` 的 Record 使用 `startedAt` 判断。
- 返回顺序固定为 `(startedAt, id)`。
- 查询不解释 Payload，不解析 Application Identity，不跨 Track 聚合，不按设备或应用分组。
- 当前不计算 `range + next_record` 的派生结束时间。

响应：

```json
{
  "track": {
    "id": "019e0000-0000-7000-8000-000000000002",
    "collectorId": "019e0000-0000-7000-8000-000000000001",
    "type": "desktop.application.foreground",
    "version": 1,
    "timeMode": "range",
    "endMode": "explicit"
  },
  "records": [
    {
      "id": "019e0000-0000-7000-8000-000000000003",
      "startedAt": "2026-09-12T10:00:00Z",
      "endedAt": "2026-09-12T10:01:00Z",
      "observedAt": null,
      "receivedAt": "2026-09-12T10:01:05Z",
      "value": {
        "device_id": "device-a",
        "application": {
          "platform": "macos",
          "id_kind": "bundle_id",
          "id": "com.google.Chrome"
        }
      }
    }
  ]
}
```
