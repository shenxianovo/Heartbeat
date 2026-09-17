# 记录 HTTP 接口

状态：已实现

本文档是后端记录 HTTP 契约的权威来源。持久不变量见[存储模型](recording-storage-model.md)。写入接口由 [Hub](hub-record-delivery.md) 调用，Collector 不直接调用后端。

## 认证

Owner 来自验签令牌的 UUID `sub`。OIDC access token 与 agent session token 均受支持。缺失或非法 `sub` 返回 `401`。

客户端不能提交 Owner、Timeline、`receivedAt` 或其他服务端字段。资源不存在和不属于当前 Owner 使用相同的 `404`，避免泄露其他 Owner 的资源。

## 注册 Collector

`POST /api/v1/collectors`

```json
{
  "key": "heartbeat.collector.desktop.macos",
  "target": "device-a",
  "displayName": "My Mac"
}
```

- 首次成功注册时，后端在同一事务中自动创建 Timeline。
- `(timeline_id, key, target)` 是稳定地址；重复和并发请求复用原 Collector。
- 重复注册可以更新 `displayName`，最后成功提交者生效。
- 三个字符串去除首尾空格后非空，最长 255 个字符。
- `key` 使用小写点号分段且不含版本。
- 成功始终返回 `200`，不区分创建和复用。

```json
{
  "id": "019e0000-0000-7000-8000-000000000001",
  "key": "heartbeat.collector.desktop.macos",
  "target": "device-a",
  "displayName": "My Mac",
  "createdAt": "2026-09-12T10:00:00Z"
}
```

字段无效返回 `400`，无有效认证返回 `401`。

## 获取 Track

`POST /api/v1/collectors/{collectorId}/tracks`

```json
{
  "type": "desktop.application.foreground",
  "version": 1,
  "timeMode": "range",
  "endMode": "explicit"
}
```

- `version` 必须为正整数。
- `timeMode` 为 `point` 或 `range`。
- Point 的 `endMode` 为空；Range 的 `endMode` 为 `explicit` 或 `next_record`。
- `(collector_id, type, version)` 是唯一地址，重复和并发请求复用原 Track。
- 后端允许未知 `(type, version)`，不登记或解释 Payload。
- 已有 Track 的时间定义不同时返回 `409 track_definition_conflict`。
- Collector 不存在或不属于当前 Owner 时返回 `404 collector_not_found`。

成功返回 `200`：

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

## Track 目录

`GET /api/v1/tracks`

返回当前 Owner 的全部 Track 及 Collector 展示信息：

```json
{
  "tracks": [{
    "id": "019e0000-0000-7000-8000-000000000002",
    "collectorId": "019e0000-0000-7000-8000-000000000001",
    "collectorKey": "heartbeat.collector.desktop.macos",
    "collectorTarget": "device-a",
    "collectorDisplayName": "My Mac",
    "type": "desktop.application.foreground",
    "version": 1,
    "timeMode": "range",
    "endMode": "explicit",
    "createdAt": "2026-09-12T10:00:00Z"
  }]
}
```

无 Timeline 或 Track 时返回空数组，读取不创建对象。排序依次使用 Collector `key`、`target`，再使用 Track `type`、`version`、`id`。

## 上传 Record

`POST /api/v1/tracks/{trackId}/records`

```json
{
  "records": [{
    "id": "019e0000-0000-7000-8000-000000000003",
    "startedAt": "2026-09-12T10:00:00Z",
    "endedAt": "2026-09-12T10:01:00Z",
    "observedAt": null,
    "value": {"device_id": "device-a"}
  }]
}
```

- 一批含 1 到 500 条 Record，全部属于路径中的 Track。
- 每项只接受 `id`、`startedAt`、`endedAt`、`observedAt` 和 `value`。
- `id` 是 Collector 生成的 UUID v7，续期和重试必须复用。
- `observedAt` 为空表示等于 `startedAt`。
- `value` 可以是任意 JSON 值；缺失无效。
- Point 与 `range + next_record` 的 `endedAt` 为空。
- `range + explicit` 必须提供 `endedAt`，同一 Record 只允许单调延长。
- Track 不存在或不属于 Owner 时返回 `404 track_not_found`。
- JSON、字段、批次大小或未知字段无效时，在写入前以 `400` 拒绝整批。

服务端为每批读取一次接收时间，新 Record 保存该时间；重试和续期保留首次接收时间。

```json
{
  "results": [{
    "index": 0,
    "id": "019e0000-0000-7000-8000-000000000003",
    "status": "stored",
    "endedAt": "2026-09-12T10:01:00Z",
    "receivedAt": "2026-09-12T10:01:05Z",
    "detail": null
  }]
}
```

| 状态 | 含义 |
| --- | --- |
| `stored` | 首次写入、重复上传或合法续期成功 |
| `invalid_record` | ID、时间、Track 时间定义或 value 无效 |
| `conflict` | 同一 ID 的固定字段或结束时间形状与已有记录不同 |
| `track_not_found` | 写入时 Track 已无法按当前 Owner 找到 |

HTTP `200` 不代表全部成功，调用方必须检查每项。条目按输入顺序独立提交，基础设施失败可能发生在部分成功之后。

Hub 只能用 `stored` 确认上传进度。旧请求回执只确认发送时的快照，不能清除同一 Record 后续产生的进度。

## 查询 Record

`GET /api/v1/tracks/{trackId}/records`

| 参数 | 规则 |
| --- | --- |
| `from` | 可选 UTC 下界，包含 |
| `to` | 可选 UTC 上界，不包含；同时提供时须满足 `from < to` |
| `limit` | 默认 200，范围 1 到 500 |
| `cursor` | 原样使用上一页的 `nextCursor`；无效时返回 `400 invalid_request` |

- Track 不存在或不属于当前 Owner 时返回 `404 track_not_found`。
- 有 `endedAt` 的 Record 按区间交叠进入 `[from, to)`；其他 Record 按 `startedAt`。
- 结果按 `(startedAt, id)` 排序并做 keyset 分页。
- 每页重新验证 Owner；cursor 不提供授权。续读时保持同一时间窗。
- 查询不解释 Payload、解析 Application Identity、跨 Track 聚合或按业务字段分组。
- 当前不计算 `range + next_record` 的派生结束时间。

响应包含 `track`、`records` 和可空的 `nextCursor`。Record 字段为 `id`、`startedAt`、`endedAt`、`observedAt`、`receivedAt` 和 `value`。`nextCursor` 是不透明字符串，客户端不得解析或构造。

## Point 计数

`GET /api/v1/tracks/{trackId}/point-counts`

必填参数：

- `from`、`to`：UTC 半开窗口，`from < to`；
- `bucketSeconds`：正整数桶宽，桶从 `from` 对齐。

最多 10,000 个桶，只支持 Point Track。Range Track 返回 `400 track_is_not_point`；Track 不存在或不属于 Owner 时返回 `404 track_not_found`。

响应包含 Track、窗口、桶宽和非空桶。每个桶返回 `index`、`startedAt`、`endedAt`、`count`。未返回的桶计为零。聚合只按 `startedAt` 计数，不读取 value；原始详情仍通过 Record 分页接口读取。
