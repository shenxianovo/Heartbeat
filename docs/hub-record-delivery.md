# Hub 记录交付

已确认分工：**采集 Collector 做，交付 Hub 做，存储后端做，展示前端做。**

Collector 形成 Record；Hub 持久接管后独立完成后端注册、Track 解析、上传和重试。首次接入不需要预先获取后端 ID，也不要求后端在线。后端保存公共记录结构和任意 JSON value，具体采集内容由 Collector 定义、由读取和展示模块解释。

当前重写完成前不部署，不承担旧接口、旧数据格式或旧客户端的兼容性。

## 模块责任

- Collector：平台观测、连续性判断、Record 内容和稳定 ID，以及尚未交接的快照。
- `Heartbeat.Hub.Client`：可复用的提交数据结构和 `HubSubmissionClient`；只封装 HTTP 提交与回执核对，不依赖 SQLite 或后端项目。
- `Heartbeat.Hub`：SQLite 接管、后端 Collector/Track 映射、上传、重试与逐条回执处理。
- `Heartbeat.Hub.Host`：HTTP 接收、配置、认证与独立后台任务。没有 Collector 连接时也会恢复积压。
- 后端：Owner 归属、公共记录不变量、PostgreSQL 存储与查询。

Hub 不托管 Collector 的安装、激活、启停或升级。交付路径为 `Collector -> Hub SQLite -> 后端 PostgreSQL`，后端仍使用既定的 `Timeline -> Collector -> Track -> Record` 四层模型。

## 唯一提交入口

`POST /hub/v1/records`：

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
  "records": [
    {
      "id": "019e0000-0000-7000-8000-000000000003",
      "startedAt": "2026-09-12T10:00:00Z",
      "endedAt": "2026-09-12T10:03:00Z",
      "value": {
        "device_id": "device-a",
        "application": {
          "platform": "macos",
          "id_kind": "bundle_id",
          "id": "com.apple.finder"
        }
      }
    }
  ]
}
```

请求携带 Collector 和 Track 的逻辑声明，不携带后端 Collector ID、Track ID、Owner 或 receivedAt。

- 稳定地址由 `key + target + type + version` 确定；displayName 不参与身份。后端归属由 Hub 自身的 Owner 配置确定。
- Hub 去除声明字符串首尾空格，校验公共字段；已有地址的时间定义不允许改变。
- `point` 的 endMode 和 Record endedAt 均为空；`range + explicit` 要求 endedAt；`range + next_record` 的 endedAt 为空。
- type/version 标识数据含义，不要求后端预先登记对应的字段模式。value 可以是对象、数组、标量或 JSON null；缺失 value 无效。
- 一个提交包含同一声明下的 1–500 条 Record，请求体及规范化后的完整提交均限制为 1 MiB。
- 声明与 Record 在 SQLite 中整批事务提交。任何校验、冲突、容量或持久化失败都不会返回接管成功。

成功回执：

```json
{
  "results": [
    {
      "index": 0,
      "id": "019e0000-0000-7000-8000-000000000003",
      "status": "accepted",
      "endedAt": "2026-09-12T10:03:00Z"
    }
  ]
}
```

`accepted` 表示 Hub 已持久接管该快照，不表示后端已存入。提交客户端逐项核对 index、ID 和结束时间，全部确认后才成功返回；Collector 随后释放对应的待交接快照。仍在采集中的 Record 状态继续由 Collector 持有。

同 ID 重复提交保持固定字段不变；明确区间只合并更大的 endedAt。声明归属、开始时间、observedAt 或 value 改变时返回冲突并整批回滚。JSON 对象属性顺序、等价 UTC 偏移不构成变化；与开始时间相同的 observedAt 规范化为 null。

认证失败为 401，无效提交为 400，冲突为 409，容量或 SQLite 写入失败为 503。超时、断连或无法辨认的回执都不能释放待交接快照。

## 后端映射、上传与恢复

Hub 在 SQLite 内保存逻辑声明和后端映射。上传时若还没有映射，依次调用后端 Collector 注册和 Track 获取接口，核对返回身份与时间定义并持久保存映射，再上传记录。断网或注册回执丢失时保留数据；后端幂等注册允许继续重试。映射保存后，Hub 重启继续使用它。

displayName 在本地声明中可更新，首次注册时提交给后端；当前不会因显示名称变化重新注册已有映射。

每轮最多选取 500 条记录，按本地声明和后端请求的 1 MiB 大小限制分批。网络 I/O 期间不持有 SQLite 写事务。失败记录的尝试顺序会轮换，避免一个失败来源持续占据队首。

后端逐条回执必须匹配 index 和 Record ID。成功还必须包含首次接收时间及匹配的结束位置；明确区间按 PostgreSQL 微秒精度核对，Point 和 next_record 要求结束位置为空。

上传期间 Collector 若已将区间延长，旧回执不能清除新进度。后端已提交但回执丢失时，Hub 保留快照并按原 ID 重传，后端通过幂等写入和单调延长收敛。

网络失败、401、429、5xx 和未知 HTTP 错误保留记录并重试。已知的逐条 invalid_record、conflict、track_not_found，以及已知的整批 invalid_request/track_not_found，暂停对应记录并保留错误代码。暂停项继续占用容量；当前没有人工重试或删除入口。

Collector 停止不影响已接管记录。Hub 正常退出不要求网络排空，重启后独立恢复交付。Hub 本地整批接管与后端逐条提交是各自的事务范围。

## 持久性与容量

SQLite 使用 WAL，每连接设置 `synchronous=FULL`。默认最多保留 10000 个不同 Record，包含暂停项。同 Record 的续期仍可合并；容量不足时不确认新记录，不自动丢弃已有记录。

SQLite 放在本地持久磁盘的用户保护目录。凭据不写入数据库，记录内容目前不加密。运行中备份需要同时考虑 WAL。

Hub 接管前的记录不受其 SQLite 保护。当前 desktop 的待交接快照仍在内存，接管前退出可能丢失，长期无法交接也可能积累内存。相关未决问题见[记录模型未决设计](recording-open-questions.md)。

## 配置与运行

后端要求 Owner 的 Timeline 已存在；Collector 注册与 Track 获取由 Hub 自动完成。

| 变量 | 含义 |
| --- | --- |
| `Hub__DatabasePath` | 当前 Hub 的 SQLite 路径 |
| `Hub__BackendUrl` | 后端 HTTP(S) origin，不包含路径 |
| `Hub__OwnerId` | Hub 所属 Owner UUID |
| `Hub__BackendToken` | 该 Owner 的后端 JWT |
| `Hub__AccessToken` | 独立本地接入密钥，至少 32 字符 |
| `Hub__MaximumRecords` | 容量，默认 10000 |
| `Hub__UploadIntervalSeconds` | 上传间隔，默认 5 秒，范围 1–3600 |

```bash
dotnet run --project src/Hub/Heartbeat.Hub.Host
```

默认监听 `http://127.0.0.1:4318`。所有接口要求 `Authorization: Bearer <Hub__AccessToken>`；Collector 无需后端 JWT。HTTP 客户端不自动跨地址重定向。远端部署需提供受保护的传输路径。

每个 SQLite 文件固定绑定后端 origin 和 Owner，不允许换绑。同 Owner 的后端 JWT 可以更换并重启生效。Hub 检查 JWT sub 只用于防止误路由，不验签；完整认证仍由后端负责。

desktop 接入：

```bash
dotnet run --project src/Collectors/Heartbeat.Collector.Desktop.Mac -- \
  --hub http://127.0.0.1:4318 \
  --hub-token "$HEARTBEAT_HUB_TOKEN" \
  --target "$HEARTBEAT_COLLECTOR_TARGET"
```

`--once` 等待一次 Hub 持久接管，不等待后端上传。首次运行也无需预先注册或获取 Track。

诊断接口 `GET /hub/v1/status` 返回 pending/failed 条数；`GET /hub/v1/failures` 返回前 500 个暂停项与错误代码。两者都受本地密钥保护。

## 验证

自动测试覆盖接管事务、容量与归属、并发续期、错误和旧回执、首次离线接入、映射恢复、按字节分批，以及真实 PostgreSQL 写入、丢失回执重传和任意 JSON 回放。

```bash
dotnet test Heartbeat.slnx --no-restore --verbosity quiet
```

2026-09-12 本轮全量回归 172/172 通过，零失败、零跳过：领域 26、desktop 25、Hub 41、PostgreSQL 集成 80。

2026-09-12 原生 macOS 冒烟使用临时数据库、测试凭据和不可达后端：desktop 未提供后端 ID，`--once` 成功提交实际前台应用记录；强制终止并重启 Hub 后，相同 Record ID 仍在队列，映射保持未解析。临时进程和数据已清理。该验证是进程终止恢复，不是物理断电测试。
