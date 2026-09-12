# 批量 Record 上传

状态：已实现并验证

## 目标

打通已认证 Owner 注册 Collector、获取 Track、上传已确认区间的后端链路。复用现有原子存储，先实现 HTTP 批量契约，不引入批量 SQL、任务队列或新表。

## 请求

`POST /api/v1/tracks/{trackId}/records`

```json
{
  "records": [
    {
      "id": "019e0000-0000-7000-8000-000000000001",
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

- 一批属于同一个既有 Track，包含 1–500 条；不同 Track 分批上传。
- 每项只接受 `id`、`startedAt`、`endedAt`、可选的 `observedAt` 和 `value`。客户端不得提供 `receivedAt`、`trackId`、Owner 或时间模式。
- `id` 必须是 Collector 生成的 UUID v7。`startedAt` 必填；缺失不会被默认为最小时间。`endedAt` 必须符合 Track 时间行为且不得早于开始。
- `observedAt` 省略或为 null 时表示与开始相同；明确传入与开始相同的时刻也规范化为空。
- 服务端读取一次批次接收时间，用于该批中新记录的 `received_at`。重试与续期保留已存的首次接收时间。
- 首期上传仅支持代码注册为允许续期的协议，当前为 `desktop.application.foreground` v1。未知协议、未支持的协议或版本返回 `400 unsupported_protocol`；已有 Track 时间模式与协议不一致返回 `409 track_protocol_conflict`。
- Owner 从已验证令牌取得。缺失或不属于该 Owner 的 Track 统一返回 `404 track_not_found`；未认证返回 `401`。不隐式创建任何父级。

## 逐条确认

结构合法的批次按输入顺序处理。每条记录先校验协议和领域约束，再调用已有原子存储。预期的单条失败不阻止后续条目；各条独立提交，没有整批事务。

处理完成返回 `200`，客户端必须检查每项状态，不能将 HTTP 200 等同于全部成功：

```json
{
  "results": [
    {
      "index": 0,
      "id": "019e0000-0000-7000-8000-000000000001",
      "status": "stored",
      "endedAt": "2026-09-12T10:01:00Z",
      "receivedAt": "2026-09-12T10:01:05Z",
      "detail": null
    }
  ]
}
```

| 状态 | 含义 |
| --- | --- |
| `stored` | 本次写入成功；首次写入、重复上传和续期共用此状态。返回本次确认的 `endedAt` 和首次接收时间。 |
| `invalid_record` | ID、时间或载荷不符合协议；未写入。`detail` 提供原因，时间回执为空。 |
| `conflict` | 同一个 ID 的固定字段与已存记录不同；未改动已有记录，时间回执为空。 |
| `track_not_found` | 批次读取 Track 后，存储入口已无法找到归属当前 Owner 的 Track；本条未写入。 |

- `index` 从 0 开始，与输入位置一一对应；null 条目返回 `invalid_record` 且 `id` 为 null。
- 同一 ID 可以在同一批出现多次，按顺序独立确认，不静默合并或丢弃条目。后续请求可能继续延长区间，所以回执只表示该项写入时确认的进度。
- JSON 解析失败、DTO 字段类型错误、未知请求字段、缺失或空批次、超过条数上限，均在逐条写入前拒绝整批。参数校验错误返回 `400 invalid_request`，JSON 绑定错误返回 `400` Problem Details。
- 未预期的基础设施错误返回 `500`，请求取消或连接中断也可能导致没有完整回执；此前成功提交的条目仍然存在。Collector 必须保留原 ID 与待上传内容重试，不能假设整批均未写入。
- Collector 只能用 `stored` 回执确认对应项的上传进度；旧请求的回执不能清除同一 Record 后来产生、尚未确认的新进度。

## 实现与验证

- Application 每批读取一次所属 Track 和协议；现有存储入口仍会逐条复核 Owner，并执行一条原子 SQL。当前是 HTTP 批量，不是数据库批量优化。
- 测试覆盖注册到上传的完整后端链路、重复 ID、乱序续期、部分成功、固定字段冲突、非法载荷、Owner 隔离、批次上限、服务端字段保护及协议不匹配。
- 故障注入验证第一条提交后第二条失败，重试整批仍得到两条记录且无重复。
- 不修改 EF 模型或 migration。重放查询、Collector 持久上传队列和设备配对仍待实现。

## Comments

- 2026-09-12：新增 18 项 HTTP/PostgreSQL 集成测试。`dotnet test Heartbeat.slnx --no-restore` 全部通过：Domain 40/40、Integration 73/73，无 warning。测试使用隔离 PostgreSQL 容器；未迁移业务数据库。已有原子写入 SQL 保持不变。
